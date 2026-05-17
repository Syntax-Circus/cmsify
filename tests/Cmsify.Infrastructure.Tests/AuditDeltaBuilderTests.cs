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
}
