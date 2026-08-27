using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence;

public sealed class CmsifyDbContext : DbContext
{
    public CmsifyDbContext(DbContextOptions<CmsifyDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Template> Templates => Set<Template>();

    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();

    public DbSet<TemplateSection> TemplateSections => Set<TemplateSection>();

    public DbSet<TemplateField> TemplateFields => Set<TemplateField>();

    public DbSet<TemplateFieldAllowedType> TemplateFieldAllowedTypes => Set<TemplateFieldAllowedType>();

    public DbSet<ComponentDefinition> Components => Set<ComponentDefinition>();
    public DbSet<ComponentVersion> ComponentVersions => Set<ComponentVersion>();
    public DbSet<ComponentField> ComponentFields => Set<ComponentField>();

    public DbSet<ContentItem> ContentItems => Set<ContentItem>();

    public DbSet<ContentFieldValue> ContentFieldValues => Set<ContentFieldValue>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

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

    public DbSet<PickList> PickLists => Set<PickList>();

    public DbSet<PickListOption> PickListOptions => Set<PickListOption>();

    public DbSet<PickListRevision> PickListRevisions => Set<PickListRevision>();

    public DbSet<PickListRevisionOption> PickListRevisionOptions => Set<PickListRevisionOption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CmsifyDbContext).Assembly);
    }
}
