using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

// PostgreSQL-only photo schema; the existing SQLite model is unchanged.
internal static class EmbeddedPhotoConfiguration
{
    public static void Ignore(ModelBuilder model)
    {
        foreach (var type in Types) model.Ignore(type);
    }

    private static readonly Type[] Types =
    [
        typeof(PhotoUploadSessionRecord), typeof(PhotoUploadAttemptRecord), typeof(PhotoOriginalRecord),
        typeof(PhotoUploadReceiptRecord), typeof(PhotoIntakeRetentionRecord), typeof(PhotoRevisionRecord), typeof(PhotoJobRecord),
        typeof(PhotoProcessingAttemptRecord), typeof(PhotoManifestRecord), typeof(PhotoVariantRecord),
        typeof(MediaReferenceSetRecord), typeof(MediaReferenceEntryRecord), typeof(PhotoOperationReceiptRecord)
    ];

    public static void Configure(ModelBuilder model)
    {
        model.Entity<MediaAsset>().HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        var session = model.Entity<PhotoUploadSessionRecord>();
        session.ToTable("embedded_photo_upload_sessions", t => t.HasCheckConstraint("ck_photo_declared_length", "declared_length > 0 AND declared_length <= 67108864"));
        session.HasKey(x => x.Id);
        session.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        session.HasIndex(x => new { x.WorkspaceId, x.OperationKey }).IsUnique();
        session.HasIndex(x => x.AssetId).IsUnique();
        session.Property(x => x.DisplayFileName).HasMaxLength(255);
        session.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        Identity(session);
        session.Property(x => x.ExpectedSourceSha256).HasColumnType("char(64)");
        Asset(session);

        var upload = model.Entity<PhotoUploadAttemptRecord>();
        upload.ToTable("embedded_photo_upload_attempts");
        upload.HasKey(x => x.Id);
        upload.HasIndex(x => new { x.SessionId, x.Generation }).IsUnique();
        upload.HasIndex(x => new { x.StorageProvider, x.StorageKey }).IsUnique();
        upload.HasOne<PhotoUploadSessionRecord>().WithMany().HasForeignKey(x => new { x.SessionId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Storage(upload);

        var original = model.Entity<PhotoOriginalRecord>();
        original.ToTable("embedded_photo_originals", t =>
        {
            t.HasCheckConstraint("ck_photo_original_length", "length > 0 AND length <= 67108864");
            t.HasCheckConstraint("ck_photo_enrollment_revision", "processing_enrollment_revision IS NULL OR processing_enrollment_revision > 1");
        });
        original.HasKey(x => x.AssetId);
        original.HasIndex(x => new { x.StorageProvider, x.StorageKey }).IsUnique();
        original.HasOne<PhotoUploadSessionRecord>().WithMany().HasForeignKey(x => new { x.SessionId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        original.Property(x => x.SourceSha256).HasColumnType("char(64)");
        original.Property(x => x.MetadataJson).HasColumnType("jsonb");
        original.HasOne<PhotoRevisionRecord>().WithMany()
            .HasForeignKey(x => new { x.AssetId, x.ProcessingEnrollmentRevision, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.AssetId, x.Revision, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Storage(original);
        Asset(original);

        var receipt = model.Entity<PhotoUploadReceiptRecord>();
        receipt.ToTable("embedded_photo_upload_receipts");
        receipt.HasKey(x => x.Id);
        receipt.HasAlternateKey(x => new { x.Id, x.AssetId, x.WorkspaceId });
        receipt.HasIndex(x => x.SessionId).IsUnique();
        receipt.HasIndex(x => new { x.WorkspaceId, x.OperationKey }).IsUnique();
        receipt.Property(x => x.SourceSha256).HasColumnType("char(64)");
        receipt.HasOne<PhotoUploadSessionRecord>().WithMany().HasForeignKey(x => new { x.SessionId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Asset(receipt);

        var intake = model.Entity<PhotoIntakeRetentionRecord>();
        intake.ToTable("embedded_photo_intake_retentions");
        intake.HasKey(x => x.AssetId);
        intake.HasIndex(x => x.UploadReceiptId).IsUnique();
        intake.HasOne<PhotoUploadReceiptRecord>().WithMany().HasForeignKey(x => new { x.UploadReceiptId, x.AssetId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.AssetId, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Asset(intake);

        var revision = model.Entity<PhotoRevisionRecord>();
        revision.ToTable("embedded_photo_revisions", t => t.HasCheckConstraint("ck_photo_revision_instruction",
            "revision > 0 AND quarter_turns_clockwise BETWEEN 0 AND 3 AND retry_generation >= 0"));
        revision.HasKey(x => new { x.AssetId, x.Revision });
        revision.HasAlternateKey(x => new { x.AssetId, x.Revision, x.WorkspaceId })
            .HasName("ak_embedded_photo_revisions_asset_id_revision_workspace_id");
        revision.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        revision.Property(x => x.SourceSha256).HasColumnType("char(64)");
        Identity(revision);
        Asset(revision);

        var job = model.Entity<PhotoJobRecord>();
        job.ToTable("embedded_photo_jobs", t => t.HasCheckConstraint("ck_photo_job_budget",
            "retry_generation >= 0 AND automatic_attempt_count BETWEEN 0 AND 3"));
        job.HasKey(x => x.Id);
        job.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        job.HasIndex(x => new { x.AssetId, x.SourceSha256, x.Revision, x.PolicyId, x.ProcessorVersion }).IsUnique();
        job.HasIndex(x => new { x.State, x.NextAttemptAt });
        job.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        job.Property(x => x.SourceSha256).HasColumnType("char(64)");
        job.HasOne<PhotoRevisionRecord>().WithMany().HasForeignKey(x => new { x.AssetId, x.Revision, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.AssetId, x.Revision, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Identity(job);

        var processing = model.Entity<PhotoProcessingAttemptRecord>();
        processing.ToTable("embedded_photo_processing_attempts");
        processing.HasKey(x => x.Id);
        processing.HasIndex(x => new { x.JobId, x.Token, x.VariantKey }).IsUnique();
        processing.HasIndex(x => new { x.StorageProvider, x.StorageKey }).IsUnique();
        processing.Property(x => x.VariantKey).HasMaxLength(32);
        processing.HasOne<PhotoJobRecord>().WithMany().HasForeignKey(x => new { x.JobId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Storage(processing);

        var manifest = model.Entity<PhotoManifestRecord>();
        manifest.ToTable("embedded_photo_manifests");
        manifest.HasKey(x => new { x.AssetId, x.Revision });
        manifest.HasAlternateKey(x => new { x.AssetId, x.Revision, x.WorkspaceId });
        manifest.HasAlternateKey(x => new { x.AssetId, x.Revision, x.WorkspaceId, x.ManifestSha256 });
        manifest.Property(x => x.ManifestSha256).HasColumnType("char(64)");
        manifest.Property(x => x.SourceSha256).HasColumnType("char(64)");
        manifest.Property(x => x.MetadataJson).HasColumnType("jsonb");
        manifest.HasOne<PhotoRevisionRecord>().WithMany().HasForeignKey(x => new { x.AssetId, x.Revision, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.AssetId, x.Revision, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Identity(manifest);

        var variant = model.Entity<PhotoVariantRecord>();
        variant.ToTable("embedded_photo_variants", t => t.HasCheckConstraint("ck_photo_variant_size",
            "width > 0 AND height > 0 AND length > 0"));
        variant.HasKey(x => new { x.AssetId, x.Revision, x.VariantKey });
        variant.HasIndex(x => new { x.StorageProvider, x.StorageKey }).IsUnique();
        variant.Property(x => x.VariantKey).HasMaxLength(32);
        variant.Property(x => x.ContentType).HasMaxLength(64);
        variant.Property(x => x.Sha256).HasColumnType("char(64)");
        variant.HasOne<PhotoManifestRecord>().WithMany().HasForeignKey(x => new { x.AssetId, x.Revision, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.AssetId, x.Revision, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        Storage(variant);

        var reference = model.Entity<MediaReferenceSetRecord>();
        reference.ToTable("embedded_media_reference_sets");
        reference.HasKey(x => x.Id);
        reference.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        reference.HasIndex(x => new { x.WorkspaceId, x.ConsumerNamespace, x.ReferenceKey }).IsUnique();
        reference.HasIndex(x => new { x.WorkspaceId, x.ConsumerNamespace, x.OperationKey }).IsUnique();
        reference.Property(x => x.ConsumerNamespace).HasMaxLength(100);
        reference.Property(x => x.InputFingerprint).HasColumnType("char(64)");
        reference.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        reference.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);

        var entry = model.Entity<MediaReferenceEntryRecord>();
        entry.ToTable("embedded_media_reference_entries", t => t.HasCheckConstraint("ck_media_reference_ordinal", "ordinal >= 0"));
        entry.HasKey(x => new { x.ReferenceSetId, x.Ordinal });
        entry.Property(x => x.ManifestSha256).HasColumnType("char(64)");
        entry.HasOne<MediaReferenceSetRecord>().WithMany().HasForeignKey(x => new { x.ReferenceSetId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<PhotoManifestRecord>().WithMany().HasForeignKey(x => new { x.AssetId, x.Revision, x.WorkspaceId, x.ManifestSha256 })
            .HasPrincipalKey(x => new { x.AssetId, x.Revision, x.WorkspaceId, x.ManifestSha256 }).OnDelete(DeleteBehavior.Restrict);

        var operation = model.Entity<PhotoOperationReceiptRecord>();
        operation.ToTable("embedded_photo_operation_receipts");
        operation.HasKey(x => x.Id);
        operation.HasIndex(x => new { x.WorkspaceId, x.Kind, x.OperationKey }).IsUnique();
        operation.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        operation.Property(x => x.InputFingerprint).HasColumnType("char(64)");
        operation.Property(x => x.OutputJson).HasColumnType("jsonb");
        operation.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void Identity<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        builder.Property<string>("PolicyId").HasMaxLength(100);
        builder.Property<string>("ProcessorVersion").HasMaxLength(100);
    }
    private static void Asset<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class =>
        builder.HasOne<MediaAsset>().WithMany().HasForeignKey("AssetId", "WorkspaceId")
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
    private static void Storage<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        builder.Property<string>("StorageProvider").HasMaxLength(50);
        builder.Property<string>("StorageKey").HasMaxLength(1000);
    }
}
