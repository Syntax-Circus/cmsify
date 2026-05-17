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

    public DbSet<ContentItem> ContentItems => Set<ContentItem>();

    public DbSet<ContentFieldValue> ContentFieldValues => Set<ContentFieldValue>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<ContentItemTag> ContentItemTags => Set<ContentItemTag>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<ApiClient> ApiClients => Set<ApiClient>();

    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDeliveryLog> WebhookDeliveryLogs => Set<WebhookDeliveryLog>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CmsifyDbContext).Assembly);
    }
}
