using Microsoft.Extensions.Options;

namespace MraReporting.Infrastructure;

/// <summary>
/// The current date and time in Malawi. Every "today", "yesterday" and "this month"
/// in the app comes from here, never from the server's own clock or the model's memory.
/// </summary>
public sealed class AppClock
{
    private readonly TimeZoneInfo _zone;

    public AppClock(IOptions<AppOptions> options)
    {
        _zone = Resolve(options.Value.TimeZone);
    }

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _zone);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    private static TimeZoneInfo Resolve(string id)
    {
        // Windows understands IANA ids like "Africa/Blantyre" on .NET 6+; fall back to
        // another UTC+2 zone without daylight saving if it does not.
        foreach (var candidate in new[] { id, "South Africa Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("CAT", TimeSpan.FromHours(2), "Central Africa Time", "Central Africa Time");
    }
}
