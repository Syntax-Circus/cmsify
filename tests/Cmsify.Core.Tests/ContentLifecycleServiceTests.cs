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
        var item = new ContentItem
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Approved
        };

        await new ContentLifecycleService().TransitionAsync(item, ContentStatus.Published, actorId);

        Assert.Equal(ContentStatus.Published, item.Status);
        Assert.Equal(actorId, item.UpdatedByUserId);
        Assert.NotNull(item.PublishedAt);
    }

    [Fact]
    public async Task TransitionAsync_Throws_ForInvalidTransition()
    {
        var item = new ContentItem
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Draft
        };

        await Assert.ThrowsAsync<DomainException>(() => new ContentLifecycleService().TransitionAsync(item, ContentStatus.Published, Guid.CreateVersion7()));
    }
}
