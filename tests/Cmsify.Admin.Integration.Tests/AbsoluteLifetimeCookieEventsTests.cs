using System.Security.Claims;
using Cmsify.Admin.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cmsify.Admin.Integration.Tests;

public sealed class AbsoluteLifetimeCookieEventsTests
{
    [Fact]
    public async Task ValidatePrincipal_RejectsPrincipal_WhenCookieExceedsMaxLifetime()
    {
        var events = BuildEvents(maxLifetimeHours: 1);
        var context = BuildContext(issuedUtc: DateTimeOffset.UtcNow.AddHours(-2));

        await events.ValidatePrincipal(context);

        context.Principal.Should().BeNull("RejectPrincipal nulls the principal");
    }

    [Fact]
    public async Task ValidatePrincipal_KeepsPrincipal_WhenWithinMaxLifetime()
    {
        var events = BuildEvents(maxLifetimeHours: 24);
        var context = BuildContext(issuedUtc: DateTimeOffset.UtcNow.AddHours(-1));

        await events.ValidatePrincipal(context);

        context.Principal.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidatePrincipal_DoesNothing_WhenIssuedUtcIsMissing()
    {
        var events = BuildEvents(maxLifetimeHours: 1);
        var context = BuildContext(issuedUtc: null);

        await events.ValidatePrincipal(context);

        context.Principal.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidatePrincipal_TreatsMissingConfigAsDefault24Hours()
    {
        var events = BuildEvents(maxLifetimeHours: null);
        var withinDefault = BuildContext(issuedUtc: DateTimeOffset.UtcNow.AddHours(-23));
        var beyondDefault = BuildContext(issuedUtc: DateTimeOffset.UtcNow.AddHours(-25));

        await events.ValidatePrincipal(withinDefault);
        await events.ValidatePrincipal(beyondDefault);

        withinDefault.Principal.Should().NotBeNull();
        beyondDefault.Principal.Should().BeNull();
    }

    private static AbsoluteLifetimeCookieEvents BuildEvents(int? maxLifetimeHours)
    {
        var settings = new Dictionary<string, string?>();
        if (maxLifetimeHours.HasValue)
        {
            settings["Admin:Auth:Session:MaxLifetimeHours"] = maxLifetimeHours.Value.ToString();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new AbsoluteLifetimeCookieEvents(configuration);
    }

    private static CookieValidatePrincipalContext BuildContext(DateTimeOffset? issuedUtc)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IAuthenticationService, NullAuthenticationService>();
        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "admin")
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var properties = new AuthenticationProperties();
        if (issuedUtc.HasValue)
        {
            properties.IssuedUtc = issuedUtc.Value;
        }

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(CookieAuthenticationHandler));

        var options = new CookieAuthenticationOptions();
        var ticket = new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme);
        return new CookieValidatePrincipalContext(httpContext, scheme, options, ticket);
    }

    private sealed class NullAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
    }
}

