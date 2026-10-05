using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MraReporting.Audit;
using MraReporting.Auth;
using MraReporting.Infrastructure;

namespace MraReporting.Pages;

/// <summary>The sign-in page for the app's own accounts (Auth:Mode = "Local").</summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly LocalUserStore _users;
    private readonly SignInGuard _guard;
    private readonly AuditLogger _audit;
    private readonly AppClock _clock;

    public LoginModel(LocalUserStore users, SignInGuard guard, AuditLogger audit, AppClock clock)
    {
        _users = users;
        _guard = guard;
        _audit = audit;
        _clock = clock;
    }

    [BindProperty] public string UserName { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public bool SignedOut { get; set; }

    public string? Error { get; private set; }
    /// <summary>Set when someone who is already signed in opens this page (for example with the Back button).</summary>
    public string? SignedInAs { get; private set; }
    public bool NoAccounts { get; private set; }
    public DateTime Now => _clock.Now;

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            SignedInAs = User.FindFirst(AppClaims.DisplayName)?.Value ?? User.Identity.Name;
            return Page();
        }
        NoAccounts = _users.Count == 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (UserName ?? "").Trim();
        NoAccounts = _users.Count == 0;

        if (name.Length == 0 || string.IsNullOrEmpty(Password))
        {
            Error = "Enter your user name and password.";
            return Page();
        }

        var locked = _guard.MinutesLocked(name);
        if (locked > 0)
        {
            Error = $"Too many attempts for this account. Try again in {locked} minute{(locked == 1 ? "" : "s")}.";
            return Page();
        }

        var user = _users.Verify(name, Password);
        Password = ""; // never echo the password back into the page
        if (user is null)
        {
            _guard.Failed(name);
            await _audit.WriteAsync(new AuditEntry(_clock.Now, name, "sign-in", "Sign-in failed", null, 0, 0, null, "wrong user name or password, or account disabled"));
            Error = "The user name or password is not correct.";
            return Page();
        }

        _guard.Succeeded(name);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim(AppClaims.DisplayName, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(AppClaims.AppRun, AppClaims.CurrentRun),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
        await _audit.WriteAsync(new AuditEntry(_clock.Now, user.UserName, "sign-in", "Signed in", null, 0, 0, null, null));

        return LocalRedirect(SafeReturnUrl());
    }

    /// <summary>Only paths inside this app, so a link cannot send people to another site after signing in.</summary>
    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && !ReturnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            ? ReturnUrl
            : "/";
}
