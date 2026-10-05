using System.Security.Claims;
using MraReporting.Auth;

namespace MraReporting.Infrastructure;

/// <summary>Who is asking. Role and station will come from MRA's directory or the UserAccess table later.</summary>
public sealed record UserContext(string UserName, string DisplayName, string Role, string? StationCode);

public sealed class UserContextAccessor
{
    public UserContext Get(HttpContext http)
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var name = user.Identity.Name ?? "unknown";
            var display = user.FindFirst(AppClaims.DisplayName)?.Value ?? name;
            var role = user.FindFirst(ClaimTypes.Role)?.Value ?? "User";
            // TODO: restrict StationCode per user once station-level access is agreed.
            return new UserContext(name, display, role, null);
        }

        // Only reachable when Auth:Mode is "Off" on a developer machine.
        var dev = $"DEV\\{Environment.UserName}";
        return new UserContext(dev, dev, "HQ", null);
    }
}
