using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Admin.Auth;

public sealed class AbsoluteLifetimeCookieEvents : CookieAuthenticationEvents
{
    private readonly TimeSpan maxLifetime;

    public AbsoluteLifetimeCookieEvents(IConfiguration configuration)
    {
        var hours = configuration.GetValue("Admin:Auth:Session:MaxLifetimeHours", 24);
        maxLifetime = TimeSpan.FromHours(Math.Max(1, hours));
    }

    public override Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var issued = context.Properties.IssuedUtc;
        if (issued.HasValue && DateTimeOffset.UtcNow - issued.Value > maxLifetime)
        {
            context.RejectPrincipal();
            return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        return Task.CompletedTask;
    }
}
