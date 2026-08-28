using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Cmsify.Admin.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using SyntaxCircus.Blazor.Auth;
using SyntaxCircus.Cmsify;
using Testcontainers.Redis;

namespace Cmsify.Admin.Integration.Tests;

public sealed class OidcDistributedTokenCacheTests : IAsyncLifetime
{
    private readonly RedisContainer redis = new RedisBuilder("redis:7.4-alpine").Build();

    public ValueTask InitializeAsync() => new(redis.StartAsync());

    public async ValueTask DisposeAsync() => await redis.DisposeAsync();

    [Fact]
    public async Task OidcDistributedTokenCache_UsesRedisAcrossIndependentProvidersWithSinglePrefixAndEviction()
    {
        var prefix = $"cmsify-test:{Guid.NewGuid():N}:";
        await using var first = new AdminAuthTestFactory
        {
            OidcEnabled = true,
            OidcRedisEnabled = true,
            OidcRedisConnectionString = redis.GetConnectionString(),
            OidcRedisInstanceName = prefix
        };
        await using var second = new AdminAuthTestFactory
        {
            OidcEnabled = true,
            OidcRedisEnabled = true,
            OidcRedisConnectionString = redis.GetConnectionString(),
            OidcRedisInstanceName = prefix
        };
        _ = first.CreateClient();
        _ = second.CreateClient();

        var firstDistributed = first.Services.GetRequiredService<IDistributedCache>();
        var secondDistributed = second.Services.GetRequiredService<IDistributedCache>();
        var firstCache = first.Services.GetRequiredService<IServerTokenCache>();
        var secondCache = second.Services.GetRequiredService<IServerTokenCache>();
        first.Services.ShouldNotBeSameAs(second.Services);
        firstDistributed.ShouldNotBeSameAs(secondDistributed);
        firstCache.ShouldNotBeSameAs(secondCache);

        var entry = new ServerTokenCacheEntry("distributed-access", "distributed-refresh", "distributed-id", DateTimeOffset.UtcNow.AddMinutes(5));
        await firstCache.SetAsync("user:oidc-admin", entry);
        var shared = await secondCache.GetAsync("user:oidc-admin");
        shared.ShouldBe(entry);

        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var database = connection.GetDatabase();
        var raw = await database.HashGetAsync(prefix + "user:oidc-admin", "data");
        raw.HasValue.ShouldBeTrue();
        (await database.KeyExistsAsync(prefix + prefix + "user:oidc-admin")).ShouldBeFalse();
        var payload = JsonDocument.Parse(raw.ToString());
        payload.RootElement.GetProperty("accessToken").GetString().ShouldBe("distributed-access");
        payload.RootElement.GetProperty("refreshToken").GetString().ShouldBe("distributed-refresh");
        payload.RootElement.GetProperty("idToken").GetString().ShouldBe("distributed-id");
        payload.RootElement.TryGetProperty("expiresAtUtc", out _).ShouldBeTrue();

        await firstCache.RemoveAsync("user:oidc-admin");
        (await secondCache.GetAsync("user:oidc-admin")).ShouldBeNull();
        (await database.KeyExistsAsync(prefix + "user:oidc-admin")).ShouldBeFalse();
    }

    [Fact]
    public async Task OidcDistributedTokenCache_RenderedSecondProviderRefreshesThenForgetsTheSharedCircuitToken()
    {
        var prefix = $"cmsify-refresh:{Guid.NewGuid():N}:";
        await using var first = new AdminAuthTestFactory
        {
            OidcEnabled = true,
            OidcRedisEnabled = true,
            OidcRedisConnectionString = redis.GetConnectionString(),
            OidcRedisInstanceName = prefix
        };
        await using var second = new AdminAuthTestFactory
        {
            OidcEnabled = true,
            OidcRedisEnabled = true,
            OidcRedisConnectionString = redis.GetConnectionString(),
            OidcRedisInstanceName = prefix,
            UseCircuitAuthenticationStateProvider = true,
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent)
        };
        _ = first.CreateClient();
        _ = second.CreateClient();

        var firstDistributed = first.Services.GetRequiredService<IDistributedCache>();
        var secondDistributed = second.Services.GetRequiredService<IDistributedCache>();
        var firstCache = first.Services.GetRequiredService<IServerTokenCache>();
        var secondCache = second.Services.GetRequiredService<IServerTokenCache>();
        first.Services.ShouldNotBeSameAs(second.Services);
        firstDistributed.ShouldNotBeSameAs(secondDistributed);
        firstCache.ShouldNotBeSameAs(secondCache);
        await firstCache.SetAsync(
            "user:oidc-admin",
            new ServerTokenCacheEntry("expired-access", "distributed-refresh", "distributed-id", DateTimeOffset.UtcNow.AddMinutes(-1)));

        await RenderAsync<DistributedRefreshCircuitProbe>(second);

        second.OidcTokenRequests.ShouldContain(request =>
            request.GrantType == "refresh_token" && request.RefreshToken == "distributed-refresh");
        (second.ObservedRequests.Single(request => request.RequestUri!.AbsolutePath == "/test/distributed-refresh")
            .Headers.Authorization?.ToString()).ShouldBe("Bearer refreshed-access-token");
        var replacement = await firstCache.GetAsync("user:oidc-admin");
        replacement.ShouldNotBeNull();
        replacement.AccessToken.ShouldBe("refreshed-access-token");
        replacement.RefreshToken.ShouldBe("refresh-token");
        replacement.ExpiresAtUtc.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var database = connection.GetDatabase();
        var raw = await database.HashGetAsync(prefix + "user:oidc-admin", "data");
        raw.HasValue.ShouldBeTrue();
        var payload = JsonDocument.Parse(raw.ToString());
        payload.RootElement.GetProperty("accessToken").GetString().ShouldBe("refreshed-access-token");
        payload.RootElement.GetProperty("refreshToken").GetString().ShouldBe("refresh-token");
        payload.RootElement.TryGetProperty("expiresAtUtc", out _).ShouldBeTrue();

        await firstCache.RemoveAsync("user:oidc-admin");
        (await secondCache.GetAsync("user:oidc-admin")).ShouldBeNull();
        (await database.KeyExistsAsync(prefix + "user:oidc-admin")).ShouldBeFalse();

        var requestCountAfterEviction = second.ObservedRequests.Count;
        await RenderAsync<DistributedAfterEvictionCircuitProbe>(second);
        var afterEviction = second.ObservedRequests.Skip(requestCountAfterEviction).Single();
        (afterEviction.Headers.Authorization?.ToString()).ShouldBeNull();
    }

    private static async Task RenderAsync<TComponent>(AdminAuthTestFactory factory)
        where TComponent : ComponentBase
    {
        using var renderScope = factory.Services.CreateScope();
        renderScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = OidcUser();
        await using var renderer = new HtmlRenderer(renderScope.ServiceProvider, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TComponent>());
    }

    private static ClaimsPrincipal OidcUser() => new(new ClaimsIdentity(
        [new Claim("sub", "oidc-admin"), new Claim(CmsifyAuthClaims.OidcSession, "true")],
        "oidc"));

    private sealed class DistributedRefreshCircuitProbe : DistributedCircuitProbe
    {
        protected override string Path => "/test/distributed-refresh";
    }

    private sealed class DistributedAfterEvictionCircuitProbe : DistributedCircuitProbe
    {
        protected override string Path => "/test/distributed-after-eviction";
    }

    private abstract class DistributedCircuitProbe : ComponentBase
    {
        [Inject] private CmsifyClient Cmsify { get; set; } = null!;

        protected abstract string Path { get; }

        protected override async Task OnInitializedAsync()
        {
            await Cmsify.GetAsync<object>(Path, CancellationToken.None);
        }
    }
}
