using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Exceptions;
using Cmsify.Core.Services;

namespace Cmsify.Core.Tests;

public sealed class ContentLifecycleServiceTests
{
    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Review)]
    [InlineData(ContentStatus.Review, ContentStatus.Draft)]
    [InlineData(ContentStatus.Review, ContentStatus.Approved)]
    [InlineData(ContentStatus.Approved, ContentStatus.Published)]
    [InlineData(ContentStatus.Published, ContentStatus.Archived)]
    [InlineData(ContentStatus.Archived, ContentStatus.Draft)]
    public void CanTransition_ReturnsTrue_ForAllowedTransitions(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.True(service.CanTransition(from, to));
    }

    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Published)]
    [InlineData(ContentStatus.Approved, ContentStatus.Draft)]
    [InlineData(ContentStatus.Archived, ContentStatus.Published)]
    public void CanTransition_ReturnsFalse_ForInvalidTransitions(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.False(service.CanTransition(from, to));
    }

    [Fact]
    public async Task TransitionAsync_UpdatesStatusAndActor_ForAllowedTransition()
    {
        var actorId = Guid.CreateVersion7();
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Approved
        };

        await new ContentLifecycleService().TransitionAsync(version, ContentStatus.Published, actorId);

        Assert.Equal(ContentStatus.Published, version.Status);
        Assert.Equal(actorId, version.UpdatedByUserId);
        Assert.NotNull(version.PublishedAt);
    }

    [Fact]
    public async Task TransitionAsync_Throws_ForInvalidTransition()
    {
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Draft
        };

        await Assert.ThrowsAsync<DomainException>(() => new ContentLifecycleService().TransitionAsync(version, ContentStatus.Published, Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Published)]
    [InlineData(ContentStatus.Review, ContentStatus.Published)]
    public void CanTransition_ReturnsTrue_ForOverrideTransitions_WhenAllowed(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.True(service.CanTransition(from, to, allowOverride: true));
    }

    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Published)]
    [InlineData(ContentStatus.Review, ContentStatus.Published)]
    public void CanTransition_ReturnsFalse_ForOverrideTransitions_WhenNotAllowed(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.False(service.CanTransition(from, to));
    }

    [Fact]
    public void CanTransition_ReturnsFalse_ForArchivedToPublished_EvenWithOverride()
    {
        var service = new ContentLifecycleService();

        Assert.False(service.CanTransition(ContentStatus.Archived, ContentStatus.Published, allowOverride: true));
    }

    [Fact]
    public async Task TransitionAsync_UpdatesStatus_ForOverrideTransition_WhenAllowed()
    {
        var actorId = Guid.CreateVersion7();
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Draft
        };

        await new ContentLifecycleService().TransitionAsync(version, ContentStatus.Published, actorId, allowOverride: true);

        Assert.Equal(ContentStatus.Published, version.Status);
        Assert.Equal(actorId, version.UpdatedByUserId);
        Assert.NotNull(version.PublishedAt);
    }

    [Fact]
    public async Task TransitionAsync_SetsArchivedAt_ForPublishedToArchived()
    {
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        await new ContentLifecycleService().TransitionAsync(version, ContentStatus.Archived, Guid.CreateVersion7());

        Assert.Equal(ContentStatus.Archived, version.Status);
        Assert.NotNull(version.ArchivedAt);
    }
}
