using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Cmsify.Infrastructure.Persistence.Providers;

namespace Cmsify.Infrastructure.Persistence;

public sealed class CmsifyDbContext : DbContext
{
    public CmsifyDbContext(DbContextOptions<CmsifyDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<EmbeddedContent.EmbeddedTemplateRegistration> EmbeddedTemplateRegistrations => Set<EmbeddedContent.EmbeddedTemplateRegistration>();
    public DbSet<EmbeddedContent.EmbeddedContentReceipt> EmbeddedContentReceipts => Set<EmbeddedContent.EmbeddedContentReceipt>();

    public DbSet<Template> Templates => Set<Template>();

    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();

    public DbSet<TemplateSection> TemplateSections => Set<TemplateSection>();

    public DbSet<TemplateField> TemplateFields => Set<TemplateField>();

    public DbSet<TemplateFieldAllowedType> TemplateFieldAllowedTypes => Set<TemplateFieldAllowedType>();

    public DbSet<ComponentDefinition> Components => Set<ComponentDefinition>();
    public DbSet<ComponentVersion> ComponentVersions => Set<ComponentVersion>();
    public DbSet<ComponentField> ComponentFields => Set<ComponentField>();

    public DbSet<ContentItem> ContentItems => Set<ContentItem>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<EmbeddedMedia.PhotoUploadSessionRecord> PhotoUploadSessions => Set<EmbeddedMedia.PhotoUploadSessionRecord>();
    public DbSet<EmbeddedMedia.PhotoUploadAttemptRecord> PhotoUploadAttempts => Set<EmbeddedMedia.PhotoUploadAttemptRecord>();
    public DbSet<EmbeddedMedia.PhotoOriginalRecord> PhotoOriginals => Set<EmbeddedMedia.PhotoOriginalRecord>();
    public DbSet<EmbeddedMedia.PhotoUploadReceiptRecord> PhotoUploadReceipts => Set<EmbeddedMedia.PhotoUploadReceiptRecord>();
    public DbSet<EmbeddedMedia.PhotoRevisionRecord> PhotoRevisions => Set<EmbeddedMedia.PhotoRevisionRecord>();
    public DbSet<EmbeddedMedia.PhotoJobRecord> PhotoJobs => Set<EmbeddedMedia.PhotoJobRecord>();
    public DbSet<EmbeddedMedia.PhotoProcessingAttemptRecord> PhotoProcessingAttempts => Set<EmbeddedMedia.PhotoProcessingAttemptRecord>();
    public DbSet<EmbeddedMedia.PhotoManifestRecord> PhotoManifests => Set<EmbeddedMedia.PhotoManifestRecord>();
    public DbSet<EmbeddedMedia.PhotoVariantRecord> PhotoVariants => Set<EmbeddedMedia.PhotoVariantRecord>();
    public DbSet<EmbeddedMedia.MediaReferenceSetRecord> MediaReferenceSets => Set<EmbeddedMedia.MediaReferenceSetRecord>();
    public DbSet<EmbeddedMedia.MediaReferenceEntryRecord> MediaReferenceEntries => Set<EmbeddedMedia.MediaReferenceEntryRecord>();
    public DbSet<EmbeddedMedia.PhotoOperationReceiptRecord> PhotoOperationReceipts => Set<EmbeddedMedia.PhotoOperationReceiptRecord>();

    public DbSet<MediaDeletionIntent> MediaDeletionIntents => Set<MediaDeletionIntent>();

    public DbSet<MediaReconciliationCheckpoint> MediaReconciliationCheckpoints => Set<MediaReconciliationCheckpoint>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<ContentItemTag> ContentItemTags => Set<ContentItemTag>();

    public DbSet<ContentVersion> ContentVersions => Set<ContentVersion>();

    public DbSet<ContentVersionFieldValue> ContentVersionFieldValues => Set<ContentVersionFieldValue>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserWorkspaceAccess> UserWorkspaceAccesses => Set<UserWorkspaceAccess>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<ApiClient> ApiClients => Set<ApiClient>();

    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDeliveryLog> WebhookDeliveryLogs => Set<WebhookDeliveryLog>();

    public DbSet<WebhookOutboxEvent> WebhookOutboxEvents => Set<WebhookOutboxEvent>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<IpBanEvent> IpBanEvents => Set<IpBanEvent>();

    public DbSet<PickList> PickLists => Set<PickList>();

    public DbSet<PickListOption> PickListOptions => Set<PickListOption>();

    public DbSet<PickListRevision> PickListRevisions => Set<PickListRevision>();

    public DbSet<PickListRevisionOption> PickListRevisionOptions => Set<PickListRevisionOption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CmsifyDbContext).Assembly);
        if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            EmbeddedMedia.EmbeddedPhotoConfiguration.Configure(modelBuilder);
        else
            EmbeddedMedia.EmbeddedPhotoConfiguration.Ignore(modelBuilder);
        ProviderModel.Configure(modelBuilder);
        Configurations.CmsifyModelComparers.Configure(modelBuilder);
    }

    private ICmsifyProviderModel ProviderModel => Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresCmsifyProviderModel(),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteCmsifyProviderModel(),
        _ => throw new NotSupportedException($"Unsupported Cmsify persistence provider: {Database.ProviderName}")
    };

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ProviderModel.PrepareChanges(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ProviderModel.PrepareChanges(ChangeTracker);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
