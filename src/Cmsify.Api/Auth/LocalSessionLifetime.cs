namespace Cmsify.Api.Auth;

internal static class LocalSessionLifetime
{
    public const string ExpiresAtHeaderName = "X-Session-Expires-At";

    public static DateTimeOffset CalculateExpiresAt(IConfiguration configuration, DateTimeOffset now)
    {
        var slidingWindowMinutes = configuration.GetValue("Auth:SessionSlidingExpiryMinutes", 0);
        if (slidingWindowMinutes > 0)
        {
            return now.AddMinutes(slidingWindowMinutes);
        }

        return now.AddHours(configuration.GetValue("Auth:SessionAbsoluteExpiryHours", 8));
    }
}
