namespace MraReporting.Auth;

/// <summary>Claim types the app adds at sign-in, beyond the standard name and role.</summary>
public static class AppClaims
{
    public const string DisplayName = "mra:display_name";

    /// <summary>Which run of the app issued the sign-in. A restart makes every earlier sign-in invalid.</summary>
    public const string AppRun = "mra:app_run";

    /// <summary>A new value each time the app starts.</summary>
    public static readonly string CurrentRun = Guid.NewGuid().ToString("N");
}
