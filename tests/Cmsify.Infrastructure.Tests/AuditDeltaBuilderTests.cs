using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Tests;

public sealed class AuditDeltaBuilderTests
{
    [Fact]
    public void Build_ReturnsBeforeAndAfterValues_ForModifiedProperties()
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql("Host=localhost;Database=cmsify;Username=cmsify;Password=cmsify")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new CmsifyDbContext(options);
        var workspace = new Workspace { Name = "Old", Slug = "old" };
        var entry = context.Workspaces.Attach(workspace);

        entry.Property(entity => entity.Name).OriginalValue = "Old";
        workspace.Name = "New";
        entry.Property(entity => entity.Name).IsModified = true;

        var delta = AuditDeltaBuilder.Build(entry);

        Assert.NotNull(delta);
        Assert.Equal("Old", delta.Value.GetProperty(nameof(Workspace.Name)).GetProperty("before").GetString());
        Assert.Equal("New", delta.Value.GetProperty(nameof(Workspace.Name)).GetProperty("after").GetString());
    }


    [Fact]
    public void Fingerprint_IsRedactedPrefixPlusFirstTwelveSha256HexChars()
    {
        // sha256("password") = 5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8
        Assert.Equal("redacted:5e884898da28", AuditDeltaBuilder.Fingerprint("password"));
    }

    [Fact]
    public void Build_RedactsPasswordHash_ForAddedUser()
    {
        using var context = CreateContext();
        var entry = context.Users.Add(new User { Email = "a@example.test", DisplayName = "A", PasswordHash = "$2a$12$secret-hash-value" });

        var delta = AuditDeltaBuilder.Build(entry);

        Assert.NotNull(delta);
        var after = delta.Value.GetProperty(nameof(User.PasswordHash)).GetProperty("after").GetString();
        Assert.Equal(AuditDeltaBuilder.Fingerprint("$2a$12$secret-hash-value"), after);
        Assert.DoesNotContain("secret-hash-value", delta.Value.GetRawText());
        Assert.Equal("a@example.test", delta.Value.GetProperty(nameof(User.Email)).GetProperty("after").GetString());
    }

    [Fact]
    public void Build_RedactsPasswordHashBeforeAndAfter_ForModifiedUser_AndShowsChange()
    {
        using var context = CreateContext();
        var user = new User { Email = "a@example.test", DisplayName = "A", PasswordHash = "old-hash" };
        var entry = context.Users.Attach(user);
        entry.Property(entity => entity.PasswordHash).OriginalValue = "old-hash";
        user.PasswordHash = "new-hash";
        entry.Property(entity => entity.PasswordHash).IsModified = true;

        var delta = AuditDeltaBuilder.Build(entry);

        Assert.NotNull(delta);
        var property = delta.Value.GetProperty(nameof(User.PasswordHash));
        Assert.Equal(AuditDeltaBuilder.Fingerprint("old-hash"), property.GetProperty("before").GetString());
        Assert.Equal(AuditDeltaBuilder.Fingerprint("new-hash"), property.GetProperty("after").GetString());
        Assert.NotEqual(property.GetProperty("before").GetString(), property.GetProperty("after").GetString());
        Assert.DoesNotContain("\"old-hash\"", delta.Value.GetRawText());
    }

    [Fact]
    public void Build_RedactsPasswordHash_ForDeletedUser()
    {
        using var context = CreateContext();
        var entry = context.Users.Attach(new User { Email = "a@example.test", DisplayName = "A", PasswordHash = "gone-hash" });
        entry.State = EntityState.Deleted;

        var delta = AuditDeltaBuilder.Build(entry);

        Assert.NotNull(delta);
        Assert.Equal(
            AuditDeltaBuilder.Fingerprint("gone-hash"),
            delta.Value.GetProperty(nameof(User.PasswordHash)).GetProperty("before").GetString());
    }

    [Fact]
    public void Build_RedactsWebhookEndpointSecret()
    {
        using var context = CreateContext();
        var entry = context.WebhookEndpoints.Add(new WebhookEndpoint { Name = "Hook", Url = "https://example.test/hook", Secret = "whsec_plaintext" });

        var delta = AuditDeltaBuilder.Build(entry);

        Assert.NotNull(delta);
        Assert.Equal(
            AuditDeltaBuilder.Fingerprint("whsec_plaintext"),
            delta.Value.GetProperty(nameof(WebhookEndpoint.Secret)).GetProperty("after").GetString());
        Assert.DoesNotContain("whsec_plaintext", delta.Value.GetRawText());
        Assert.Equal("Hook", delta.Value.GetProperty(nameof(WebhookEndpoint.Name)).GetProperty("after").GetString());
    }

    private static CmsifyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql("Host=localhost;Database=cmsify;Username=cmsify;Password=cmsify")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CmsifyDbContext(options);
    }
}
