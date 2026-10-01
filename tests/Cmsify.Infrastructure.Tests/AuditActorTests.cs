using System.Security.Claims;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Interceptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class AuditActorTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ScopedHostActor_WithoutHttpContextPersistsOnlyAuthenticatedIdentity(bool authenticated, bool apiClient)
    {
        var id = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor>(_ => new CurrentActorInfo(apiClient ? null : id, apiClient ? id : null,
            UserRole.Admin, null, authenticated));
        services.AddCmsifyInfrastructure(Configuration(), new CmsifyInfrastructureOptions
        {
            UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var interceptor = scope.ServiceProvider.GetRequiredService<AuditInterceptor>();
        var log = await PersistAuditAsync(interceptor);
        log.ActorUserId.ShouldBe(authenticated && !apiClient ? id : null);
        log.ActorApiClientId.ShouldBe(authenticated && apiClient ? id : null);
        scope.ServiceProvider.GetRequiredService<ICurrentActor>().ShouldBeOfType<CurrentActorInfo>();
    }

    [Fact]
    public async Task BackgroundActor_WithNoHttpContextPersistsNullIdentity()
    {
        var http = Substitute.For<IHttpContextAccessor>();
        var log = await PersistAuditAsync(new AuditInterceptor(new HttpAuditActorAccessor(http)));
        log.ActorUserId.ShouldBeNull();
        log.ActorApiClientId.ShouldBeNull();
    }

    [Fact]
    public async Task HostAudit_UsesFreshScopedActorAndIgnoresHttpClaims()
    {
        var http = Substitute.For<IHttpContextAccessor>();
        var context = Substitute.For<HttpContext>();
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("cmsify_api_client_id", Guid.NewGuid().ToString())
        }, "test")));
        context.Items.Returns(new Dictionary<object, object?>());
        http.HttpContext.Returns(context);
        var services = new ServiceCollection();
        services.AddSingleton(http);
        services.AddScoped<ICurrentActor>(_ => new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Reader, null, true));
        services.AddCmsifyInfrastructure(Configuration(), new CmsifyInfrastructureOptions
        {
            UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None
        });
        using var provider = services.BuildServiceProvider();
        Guid? previous = null;
        for (var index = 0; index < 2; index++)
        {
            using var scope = provider.CreateScope();
            var actor = scope.ServiceProvider.GetRequiredService<ICurrentActor>();
            var log = await PersistAuditAsync(scope.ServiceProvider.GetRequiredService<AuditInterceptor>());
            log.ActorUserId.ShouldBe(actor.UserId);
            log.ActorApiClientId.ShouldBeNull();
            log.ActorUserId.ShouldNotBe(previous);
            previous = actor.UserId;
        }
    }

    [Fact]
    public async Task DefaultAudit_PreservesHttpModeWhenHostActorIsRegistered()
    {
        var id = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor>(_ => new CurrentActorInfo(id, null, UserRole.Admin, null, true));
        services.AddCmsifyInfrastructure(Configuration());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var log = await PersistAuditAsync(scope.ServiceProvider.GetRequiredService<AuditInterceptor>());
        log.ActorUserId.ShouldBeNull();
        log.ActorApiClientId.ShouldBeNull();
        scope.ServiceProvider.GetRequiredService<ICurrentActor>().UserId.ShouldBe(id);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("api")]
    [InlineData("name")]
    [InlineData("sub")]
    [InlineData("user")]
    [InlineData("invalid-name")]
    [InlineData("anonymous")]
    [InlineData("anonymous-item")]
    [InlineData("invalid-api")]
    [InlineData("item-anonymous-principal")]
    public async Task HttpFallback_PreservesItemApiClientAndUserClaimPrecedence(string scenario)
    {
        var itemUserId = Guid.NewGuid();
        var itemApiId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var nameId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var claims = new List<Claim>();
        if (scenario is "item" or "api" or "anonymous-item") claims.Add(new("cmsify_api_client_id", apiId.ToString()));
        if (scenario == "invalid-api") claims.Add(new("cmsify_api_client_id", "invalid"));
        if (scenario is not "sub" and not "user") claims.Add(new(ClaimTypes.NameIdentifier, scenario == "invalid-name" ? "invalid" : nameId.ToString()));
        if (scenario != "user") claims.Add(new("sub", subId.ToString()));
        claims.Add(new("cmsify_user_id", userId.ToString()));
        var context = Substitute.For<HttpContext>();
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, scenario is "anonymous" or "item-anonymous-principal" ? null : "test")));
        var items = new Dictionary<object, object?>();
        if (scenario is "item" or "anonymous-item" or "item-anonymous-principal") items[CurrentActorHttpContextKeys.ItemName] =
            new CurrentActorInfo(itemUserId, itemApiId, UserRole.Admin, null, scenario != "anonymous-item");
        context.Items.Returns(items);
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(context);

        var log = await PersistAuditAsync(new AuditInterceptor(new HttpAuditActorAccessor(http)));

        log.ActorApiClientId.ShouldBe(scenario is "item" or "item-anonymous-principal" ? itemApiId : scenario is "api" or "anonymous-item" ? apiId : null);
        log.ActorUserId.ShouldBe(scenario switch
        {
            "item" or "item-anonymous-principal" => itemUserId,
            "name" or "invalid-api" => nameId,
            "sub" => subId,
            "user" => userId,
            _ => null
        });
    }

    private static async Task<AuditLog> PersistAuditAsync(AuditInterceptor interceptor)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>().UseSqlite(connection)
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        await using (var db = new CmsifyDbContext(options))
        {
            await db.Database.EnsureCreatedAsync(ct);
            db.Workspaces.Add(new Workspace { Name = "Audit", Slug = "audit" });
            await db.SaveChangesAsync(ct);
        }
        await using var verification = new CmsifyDbContext(options);
        return await verification.AuditLogs.SingleAsync(ct);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Cmsify"] = "Host=localhost;Database=audit_registration_only"
    }).Build();
}
