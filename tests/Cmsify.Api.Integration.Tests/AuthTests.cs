using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cmsify.Api.Integration.Tests;

public sealed class AuthTests
{
    [Fact]
    public void SessionTokenHash_IsStableSha256Hex()
    {
        var hash = TokenUtility.Sha256Hash("known-token");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, TokenUtility.Sha256Hash("known-token"));
        Assert.NotEqual(hash, TokenUtility.Sha256Hash("other-token"));
    }

    [Fact]
    public void BCrypt_VerifiesPasswordHash()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("temporary-password", 12);

        Assert.True(BCrypt.Net.BCrypt.Verify("temporary-password", hash));
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong-password", hash));
    }

    [Theory]
    [InlineData(UserRole.Reader, UserRole.Admin, typeof(ForbidResult))]
    [InlineData(UserRole.Admin, UserRole.Admin, null)]
    [InlineData(UserRole.Editor, UserRole.Reader, null)]
    public async Task RequireRole_EnforcesRoleHierarchy(UserRole actorRole, UserRole requiredRole, Type? resultType)
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentActor>(new CurrentActorInfo(Guid.CreateVersion7(), null, actorRole, null, true))
            .BuildServiceProvider();
        var context = new AuthorizationFilterContext(
            new ActionContext(new DefaultHttpContext { RequestServices = services }, new RouteData(), new ActionDescriptor()),
            []);

        await new RequireRoleAttribute(requiredRole).OnAuthorizationAsync(context);

        if (resultType is null)
        {
            Assert.Null(context.Result);
        }
        else
        {
            Assert.IsType(resultType, context.Result);
        }
    }

    [Fact]
    public async Task RequireRole_RejectsAnonymousActors()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentActor>(CurrentActorInfo.Anonymous)
            .BuildServiceProvider();
        var context = new AuthorizationFilterContext(
            new ActionContext(new DefaultHttpContext { RequestServices = services }, new RouteData(), new ActionDescriptor()),
            []);

        await new RequireRoleAttribute(UserRole.Reader).OnAuthorizationAsync(context);

        Assert.IsType<UnauthorizedResult>(context.Result);
    }
}
