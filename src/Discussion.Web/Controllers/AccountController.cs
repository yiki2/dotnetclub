﻿using System;
using Discussion.Core.Models;
using Discussion.Core.Mvc;
using Discussion.Core.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Discussion.Core.Data;
using Discussion.Core.Time;
using Discussion.Web.Services;
using Discussion.Web.Services.UserManagement;
using Discussion.Web.Services.UserManagement.Exceptions;
using Discussion.Web.ViewModels;
using IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace Discussion.Web.Controllers
{
    public class AccountController : Controller
    {
        readonly UserManager<User> _userManager;
        readonly SignInManager<User> _signInManager;
        readonly ILogger<AccountController> _logger;
        readonly IRepository<User> _userRepo;
        readonly IClock _clock;
        readonly SiteSettings _settings;
        private readonly IdentityServerOptions _idpOptions;
        readonly IUserService _userService;
        private IRepository<VerifiedPhoneNumber> _phoneNumberVerificationRepo;

        public AccountController(
            UserManager<User> userManager,
            SignInManager<User> signInManager,
            IUserService userService,
            ILogger<AccountController> logger,
            IRepository<User> userRepo,
            IClock clock,
            SiteSettings settings,
            IOptions<IdentityServerOptions> idpOptions, IRepository<VerifiedPhoneNumber> phoneNumberVerificationRepo)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _logger = logger;
            _userRepo = userRepo;
            _clock = clock;
            _settings = settings;
            _phoneNumberVerificationRepo = phoneNumberVerificationRepo;
            _idpOptions = idpOptions.Value;
            _userService = userService;
        }

        [Route("/signin")]
        public IActionResult Signin([FromQuery] string returnUrl)
        {
            if (_idpOptions.IsEnabled)
            {
                throw new InvalidOperationException("启用外部身份服务时，禁止使用本地登录");
            }

            if (HttpContext.IsAuthenticated())
            {
                return RedirectTo(returnUrl);
            }
            return View();
        }

        [HttpPost]
        [Route("/signin")]
        public async Task<IActionResult> DoSignin([FromForm] UserViewModel viewModel, [FromQuery] string returnUrl)
        {
            if (HttpContext.IsAuthenticated())
            {
                return RedirectTo(returnUrl);
            }
            
            if (_idpOptions.IsEnabled)
            {            
                _logger.LogWarning("用户登录失败：{@LoginAttempt}", new {viewModel.UserName, Result = "启用外部身份服务时，禁止使用本地登录"});
                return BadRequest();
            }

            var result = Microsoft.AspNetCore.Identity.SignInResult.Failed;
            if (ModelState.IsValid)
            {
                result = await _signInManager.PasswordSignInAsync(
                    viewModel.UserName,
                    viewModel.Password,
                    isPersistent: false,
                    lockoutOnFailure: false);

                var logLevel = result.Succeeded ? LogLevel.Information : LogLevel.Warning;
                var resultDesc = result.Succeeded ? "成功" : "失败";
                _logger.Log(logLevel, $"用户登录{resultDesc}：{{@LoginAttempt}}", new {viewModel.UserName, Result = result.ToString()} );
            }
            else
            {
                _logger.LogWarning("用户登录失败：{@LoginAttempt}", new {viewModel.UserName, Result = "数据格式不正确"});
            }

            if (!result.Succeeded)
            {
                ModelState.Clear(); // 将真正的验证结果隐藏掉（如果有的话）
                ModelState.AddModelError("UserName", "用户名或密码错误");
                return View("Signin");
            }

            var user = await _userManager.FindByNameAsync(viewModel.UserName);
            user.LastSeenAt = _clock.Now.UtcDateTime;
            _userRepo.Update(user);
            return RedirectTo(returnUrl);
        }

        [Route("/external-signin")]
        [IdentityUserActionHttpFilter(IdentityUserAction.Signin)]
        public async Task<IActionResult> ExternalSignin([FromQuery] string returnUrl)
        {
            if (HttpContext.IsAuthenticated())
            {
                return RedirectTo(returnUrl);
            }
            
            if (!_idpOptions.IsEnabled)
            {            
                _logger.LogWarning("用户登录失败：{@LoginAttempt}", new {UserName = string.Empty, Result = "未启用外部身份服务"});
                return BadRequest();
            }

            var oidcResult = await HttpContext.AuthenticateAsync(OpenIdConnectDefaults.AuthenticationScheme);
            if (oidcResult?.Succeeded != true || oidcResult.Principal == null)
            {
                _logger.LogWarning("用户登录失败：{@LoginAttempt}", new {UserName = string.Empty, Result = "使用外部身份服务登录失败"});
                return RedirectTo(returnUrl);
            }

            var externalUser = oidcResult.Principal;
            var claims = externalUser.Claims.ToList();

            var userIdClaim = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.Subject) 
                              ?? claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier);
            if (userIdClaim == null)
            {
                _logger.LogWarning("用户登录失败：{@LoginAttempt}", new {UserName = string.Empty, Result = "无法从外部身份服务的回调中取得 Subject 或 NameIdentifier 身份声明的值"});
                return RedirectTo(returnUrl);
            }

            var user = _userRepo.All().FirstOrDefault(u => u.OpenIdProvider == _idpOptions.ProviderId && u.OpenId == userIdClaim.Value);
            if (user == null)
            {
                var originalUserName = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.PreferredUserName)?.Value;
                var userName = string.Concat(originalUserName ?? userIdClaim.Value, "@", _idpOptions.ProviderId);
                if (!_settings.CanRegisterNewUsers())
                {
                    const string errorMessage = "已关闭用户注册";
                    _logger.LogWarning("用户注册失败：{@RegisterAttempt}", new {username = userName, Result = errorMessage});
                    ModelState.AddModelError("UserName", errorMessage);
                    return RedirectTo(returnUrl);
                }
                var displayNameClaim = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.NickName)?.Value 
                                       ?? claims.FirstOrDefault(x => x.Type == JwtClaimTypes.Name)?.Value
                                       ?? (string.Concat(claims.FirstOrDefault(x => x.Type == JwtClaimTypes.FamilyName)?.Value ?? " ", " ", claims.FirstOrDefault(x => x.Type == JwtClaimTypes.GivenName)?.Value ?? " ")).Trim();
                var emailClaim = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.Email)?.Value;
                var emailVerifiedClaim = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.EmailVerified)?.Value;
                var emailVerified = false;
                if (!string.IsNullOrEmpty(emailClaim) && Boolean.TryParse(emailVerifiedClaim, out emailVerified))
                {
                    // nothing to do...
                }

                
                var phoneNumberClaim = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.PhoneNumber)?.Value;
                VerifiedPhoneNumber verifiedPhoneNumber = null;
                var phoneNumberVerifiedClaim = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.PhoneNumberVerified)?.Value;
                if (!string.IsNullOrEmpty(phoneNumberClaim) && Boolean.TryParse(phoneNumberVerifiedClaim, out var phoneNumberVerified) && phoneNumberVerified)
                {
                    verifiedPhoneNumber = new VerifiedPhoneNumber()
                    {
                        PhoneNumber = phoneNumberClaim
                    };
                    _phoneNumberVerificationRepo.Save(verifiedPhoneNumber);
                }

                user = new User
                {
                    UserName = userName,
                    DisplayName = string.IsNullOrWhiteSpace(displayNameClaim) ? userName : displayNameClaim,
                    CreatedAtUtc = _clock.Now.UtcDateTime,
                    EmailAddress = emailClaim,
                    EmailAddressConfirmed = emailVerified,
                    OpenId = userIdClaim.Value,
                    OpenIdProvider = _idpOptions.ProviderId,
                    LastSeenAt = _clock.Now.UtcDateTime,
                    PhoneNumberId =  verifiedPhoneNumber?.Id
                };

                var result = await _userManager.CreateAsync(user);
                if (!result.Succeeded)
                {
                    _logger.LogWarning("用户注册失败：{@LoginAttempt}", new {UserName = userName, Result = string.Join(";", result.Errors.Select(err => err.Description ?? err.Code)) });
                    return RedirectTo(returnUrl);
                }
                else
                {
                    _logger.LogInformation("用户注册成功：{@RegisterAttempt}", new {UserName = userName, UserId = user.Id});
                }
            }
            else
            {
                user.LastSeenAt = _clock.Now.UtcDateTime;
                _userRepo.Update(user);
                _logger.LogInformation("用户登录成功：{@RegisterAttempt}", new {user.UserName, Result = $"从外部身份服务 {_idpOptions.ProviderId} 登录成功"});
            }

            await HttpContext.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme);
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            await _signInManager.SignInAsync(user, false);
            return RedirectTo(returnUrl);
        }

        [HttpPost]
        [Route("/signout")]
        [Authorize]
        [IdentityUserActionHttpFilter(IdentityUserAction.SignOut)]
        public async Task<IActionResult> DoSignOut()
        {
            if (_idpOptions.IsEnabled)
            {
                throw new InvalidOperationException("启用外部身份服务时，禁止使用本地退出登录");
            }
            
            await _signInManager.SignOutAsync();
            return RedirectTo("/");
        }

        [Route("/register")]
        [IdentityUserActionHttpFilter(IdentityUserAction.Register)]
        public IActionResult Register()
        {
            if (_idpOptions.IsEnabled)
            {
                throw new InvalidOperationException("启用外部身份服务时，禁止使用本地注册");
            }
            
            if (HttpContext.IsAuthenticated())
            {
                return RedirectTo("/");
            }

            return View();
        }

        [HttpPost]
        [Route("/register")]
        public async Task<IActionResult> DoRegister(UserViewModel registerModel)
        {
            if (_idpOptions.IsEnabled)
            {            
                _logger.LogWarning("用户注册失败：{@RegisterAttempt}", new {registerModel.UserName, Result = "启用外部身份服务时，禁止注册本地账号"});
                return BadRequest();
            }
            
            if (!ModelState.IsValid)
            {
                _logger.LogInformation("用户注册失败：{@RegisterAttempt}", new {registerModel.UserName, Result = "数据格式不正确"});
                return View("Register");
            }

            if (!_settings.CanRegisterNewUsers())
            {
                const string errorMessage = "已关闭用户注册";
                _logger.LogWarning("用户注册失败：{@RegisterAttempt}", new {registerModel.UserName, Result = errorMessage});
                ModelState.AddModelError("UserName", errorMessage);
                return View("Register");
            }

            var newUser = new User
            {
                UserName = registerModel.UserName,
                DisplayName = registerModel.UserName,
                CreatedAtUtc = _clock.Now.UtcDateTime,
                LastSeenAt = _clock.Now.UtcDateTime
            };

            var result = await _userManager.CreateAsync(newUser, registerModel.Password);
            if (!result.Succeeded)
            {
                var errorMessage = string.Join(";", result.Errors.Select(err => err.Description));
                ModelState.AddModelError("UserName", errorMessage);
                _logger.LogWarning("用户注册失败：{@RegisterAttempt}", new {registerModel.UserName, Result = errorMessage});
                return View("Register");
            }

            _logger.LogInformation("用户注册成功：{@RegisterAttempt}", new {registerModel.UserName, UserId = newUser.Id});
            await _signInManager.PasswordSignInAsync(
                registerModel.UserName,
                registerModel.Password,
                isPersistent: false,
                lockoutOnFailure: true);
            return RedirectTo("/");
        }

        [Route("/forgot-password")]
        public IActionResult ForgotPassword()
        {
            if (HttpContext.IsAuthenticated())
            {
                return RedirectTo("/");
            }
            
            if (_idpOptions.IsEnabled)
            {            
                _logger.LogWarning("发送重置密码邮件失败：{@ForgotPasswordAttempt}", new { UsernameOrEmail = string.Empty, Result = "启用外部身份服务时，禁止使用本地重置密码功能"});
                return BadRequest();
            }

            return View();
        }

        [HttpPost]
        [Route("/forgot-password")]
        public async Task<ApiResponse> DoForgotPassword(ForgotPasswordModel model)
        {
            if (_idpOptions.IsEnabled)
            {            
                _logger.LogWarning("发送重置密码邮件失败：{@ForgotPasswordAttempt}", new {model.UsernameOrEmail, Result = "启用外部身份服务时，禁止使用本地重置密码功能"});
                return ApiResponse.NoContent(HttpStatusCode.BadRequest);
            }
            
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("发送重置密码邮件失败：{@ForgotPasswordAttempt}", new {model.UsernameOrEmail, Result = "数据格式不正确"});
                return ApiResponse.Error(ModelState);
            }

            try
            {
                var user = GetUserBy(model);
                await _userService.SendEmailRetrievePasswordAsync(user, Request.Scheme);
                _logger.LogInformation("发送重置密码邮件成功：{ConfirmedEmail}", user.ConfirmedEmail);
                return ApiResponse.NoContent();
            }
            catch (RetrievePasswordVerificationException e)
            {
                _logger.LogWarning("发送重置密码邮件失败：{@ForgotPasswordAttempt}", new {model.UsernameOrEmail, Result = e.Message});
                return ApiResponse.Error(e.Message);
            }
        }

        [HttpGet]
        [Route("/reset-password")]
        public IActionResult ResetPassword(ResetPasswordModel model)
        {
            if (_idpOptions.IsEnabled)
            {            
                _logger.LogWarning("重置密码失败：{@ResetPasswordAttempt}", new {model.Token, Result = "启用外部身份服务时，禁止使用本地重置密码功能"});
                return BadRequest();
            }
            
            ModelState.Clear();

            var userEmailToken = UserEmailToken.ExtractFromQueryString(model.Token);
            if (userEmailToken == null)
            {
                var errorMessage = "无法识别的凭证";
                ModelState.AddModelError(nameof(model.Token), errorMessage);
                _logger.LogWarning("重置密码失败：{@ResetPasswordAttempt}", new {model.Token, model.UserId, Result = errorMessage});
                return View("ResetPassword", model);
            }
            
            model.Token = userEmailToken.Token;
            model.UserId = userEmailToken.UserId;
            return View(model);
        }

        [HttpPost]
        [Route("/reset-password")]
        public async Task<IActionResult> DoResetPassword(ResetPasswordModel model)
        {
            if (_idpOptions.IsEnabled)
            {            
                _logger.LogWarning("重置密码失败：{@ResetPasswordAttempt}", new {model.Token, Result = "启用外部身份服务时，禁止使用本地重置密码功能"});
                return BadRequest();
            }
            
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.FindByIdAsync(model.UserId.ToString());
            if (user == null)
            {
                var errorMessage = "用户不存在";
                ModelState.AddModelError(nameof(model.UserId), errorMessage);
                _logger.LogWarning("重置密码失败：{@ResetPasswordAttempt}", new { model.Token, model.UserId, Result = errorMessage});
                return View(model);
            }

            var result = await _userManager.ResetPasswordAsync(user, model.Token, model.Password);
            if (result.Errors.Any())
            {
                var msg = string.Join(";", result.Errors.Select(e => e.Description));
                ModelState.AddModelError(nameof(model.Token), msg);
                _logger.LogWarning("重置密码失败：{@ResetPasswordAttempt}", new { model.Token, model.UserId, Result = msg});
                model.Succeeded = false;
            }
            else
            {
                _logger.LogInformation("重置密码成功：{UserName}", user.UserName);
                model.Succeeded = true;
            }

            return View(model);
        }

        User GetUserBy(ForgotPasswordModel model)
        {
            var usernameOrEmail = model.UsernameOrEmail.ToLower();

            var users = _userRepo
                .All()
                .Where(e => e.UserName.ToLower() == usernameOrEmail ||
                            e.EmailAddress != null && e.EmailAddress.ToLower() == usernameOrEmail)
                .ToList();

            if (!users.Any())
                throw new RetrievePasswordVerificationException("该用户不存在");

            var user = users.FirstOrDefault(e => e.EmailAddressConfirmed);

            if (user == null)
                throw new RetrievePasswordVerificationException("无法验证你对账号的所有权，因为之前没有已验证过的邮箱地址");

            return user;
        }


        IActionResult RedirectTo(string returnUrl)
        {
            if (string.IsNullOrEmpty(returnUrl))
            {
                returnUrl = "/";
            }

            return Redirect(returnUrl);
        }
    }
}