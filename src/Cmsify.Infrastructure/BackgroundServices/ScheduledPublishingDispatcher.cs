using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class ScheduledPublishingDispatcher(IScheduledPublishingRepository repository) : IScheduledPublishingDispatcher
{
    public Task<IReadOnlyList<ScheduledContentClaimDto>> ClaimDueAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default) =>
        repository.ClaimDueContentAsync(workerId, now, leaseDuration, limit, ct);

    public Task<bool> CompleteClaimAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct = default) =>
        repository.CompleteClaimAsync(claim, now, ct);
}
