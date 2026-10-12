using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.EmbeddedFiles;

internal static class EmbeddedFileModelConfiguration
{
    internal static void Configure(ModelBuilder m)
    {
        var s=m.Entity<FileUploadSessionRecord>();s.ToTable("embedded_file_upload_sessions");s.HasKey(x=>x.Id);
        s.HasIndex(x=>new { x.WorkspaceId,x.AssetId }).IsUnique();s.HasIndex(x=>new { x.WorkspaceId,x.ConsumerNamespace,x.OperationKey }).IsUnique();
        s.HasAlternateKey(x=>new { x.Id,x.WorkspaceId,x.AssetId });
        s.HasOne<Workspace>().WithMany().HasForeignKey(x=>x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        s.HasOne<MediaAsset>().WithMany().HasForeignKey(x=>x.AssetId).OnDelete(DeleteBehavior.Restrict);
        s.Property(x=>x.ConsumerNamespace).HasMaxLength(100);s.Property(x=>x.Fingerprint).HasMaxLength(64);s.Property(x=>x.ExpectedSha256).HasMaxLength(64);s.Property(x=>x.DisplayFileName).HasMaxLength(200);s.Property(x=>x.FailureCode).HasMaxLength(100);
        var a=m.Entity<FileUploadAttemptRecord>();a.ToTable("embedded_file_upload_attempts");a.HasKey(x=>x.Id);
        a.HasOne<FileUploadSessionRecord>().WithMany().HasForeignKey(x=>x.SessionId).OnDelete(DeleteBehavior.Restrict);
        a.Property(x=>x.StorageProvider).HasMaxLength(50);a.Property(x=>x.StorageKey).HasMaxLength(500);a.HasIndex(x=>new { x.StorageProvider,x.StorageKey }).IsUnique();
        var r=m.Entity<FileUploadReceiptRecord>();r.ToTable("embedded_file_upload_receipts");r.HasKey(x=>x.Id);r.HasIndex(x=>x.SessionId).IsUnique();r.HasIndex(x=>x.AssetId).IsUnique();
        r.HasOne<FileUploadSessionRecord>().WithMany().HasForeignKey(x=>new { x.SessionId,x.WorkspaceId,x.AssetId }).HasPrincipalKey(x=>new { x.Id,x.WorkspaceId,x.AssetId }).OnDelete(DeleteBehavior.Restrict);
        r.Property(x=>x.ConsumerNamespace).HasMaxLength(100);r.Property(x=>x.SourceSha256).HasMaxLength(64);r.Property(x=>x.StorageProvider).HasMaxLength(50);r.Property(x=>x.StorageKey).HasMaxLength(500);
        r.HasAlternateKey(x=>new { x.Id,x.AssetId });
        var set=m.Entity<FileReferenceSetRecord>();set.ToTable("embedded_file_reference_sets");set.HasKey(x=>x.Id);
        set.HasOne<Workspace>().WithMany().HasForeignKey(x=>x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        set.Property(x=>x.ConsumerNamespace).HasMaxLength(100);set.Property(x=>x.Fingerprint).HasMaxLength(64);
        set.HasIndex(x=>new { x.WorkspaceId,x.ConsumerNamespace,x.ReferenceKey }).IsUnique();
        set.HasIndex(x=>new { x.WorkspaceId,x.ConsumerNamespace,x.OperationKey }).IsUnique();
        set.HasIndex(x=>new { x.WorkspaceId,x.ConsumerNamespace,x.ReleaseOperationKey }).IsUnique();
        var entry=m.Entity<FileReferenceEntryRecord>();entry.ToTable("embedded_file_reference_entries");entry.HasKey(x=>new { x.ReferenceSetId,x.Ordinal });
        entry.Property(x=>x.SourceSha256).HasMaxLength(64);
        entry.HasOne<FileReferenceSetRecord>().WithMany().HasForeignKey(x=>x.ReferenceSetId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<FileUploadReceiptRecord>().WithMany().HasForeignKey(x=>new { x.ReceiptId,x.AssetId }).HasPrincipalKey(x=>new { x.Id,x.AssetId }).OnDelete(DeleteBehavior.Restrict);
        entry.HasIndex(x=>new { x.ReferenceSetId,x.AssetId }).IsUnique();
    }
    internal static void Ignore(ModelBuilder m)
    { m.Ignore<FileUploadSessionRecord>();m.Ignore<FileUploadAttemptRecord>();m.Ignore<FileUploadReceiptRecord>();m.Ignore<FileReferenceSetRecord>();m.Ignore<FileReferenceEntryRecord>(); }
}
