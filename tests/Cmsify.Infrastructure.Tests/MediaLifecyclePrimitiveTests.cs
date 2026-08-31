using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Storage;

namespace Cmsify.Infrastructure.Tests;

public sealed class MediaLifecyclePrimitiveTests
{
    [Fact]
    public void StorageKeyBuilder_BuildsDeterministicManagedKey()
    {
        var workspaceId = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        var assetId = Guid.Parse("018f0000-0000-7000-8000-000000000002");
        var now = DateTimeOffset.Parse("2026-08-27T12:00:00-06:00");

        var key = StorageKeyBuilder.Build(workspaceId, assetId, "../My awkward photo (1).png", now);

        key.ShouldBe($"cmsify/media/{workspaceId}/2026/08/{assetId}_My-awkward-photo-1-.png");
    }

    [Fact]
    public void MediaOperationalOptions_UseApprovedDefaults()
    {
        var options = new MediaOperationalOptions();

        options.ReconciliationIntervalSeconds.ShouldBe(300);
        options.LeaseDurationSeconds.ShouldBe(300);
        options.BatchSize.ShouldBe(100);
        options.RetryBaseSeconds.ShouldBe(30);
        options.RetryCapSeconds.ShouldBe(3_600);
        options.RetentionDays.ShouldBe(30);
        options.OrphanGraceHours.ShouldBe(24);
        options.AbandonedUploadMinutes.ShouldBe(30);
        options.ManagedPrefixes.ShouldBe(["cmsify/media/", "default/"]);
    }

    [Fact]
    public void MediaOperationalOptionsValidator_RejectsUnsafeBoundsAndForeignPrefixes()
    {
        var options = new MediaOperationalOptions
        {
            BatchSize = 0,
            RetryBaseSeconds = 3_601,
            RetryCapSeconds = 30,
            ManagedPrefixes = ["uploads/"]
        };

        var result = new MediaOperationalOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(8, 3_600)]
    [InlineData(20, 3_600)]
    public void MediaRetryBackoff_IsExponentialAndCapped(int attempt, int expectedSeconds)
    {
        MediaRetryBackoff.Calculate(attempt, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3_600))
            .ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }
}
