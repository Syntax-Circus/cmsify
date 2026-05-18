using System.Text.RegularExpressions;
using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cmsify.Infrastructure.Tests;

public sealed partial class ModelConfigurationTests
{
    [Fact]
    public void Model_MapsAllSchemaEntities()
    {
        var model = BuildModel();
        var mappedTypes = new[]
        {
            typeof(Workspace),
            typeof(Template),
            typeof(TemplateVersion),
            typeof(TemplateSection),
            typeof(TemplateField),
            typeof(TemplateFieldAllowedType),
            typeof(ContentItem),
            typeof(ContentFieldValue),
            typeof(MediaAsset),
            typeof(Tag),
            typeof(ContentItemTag),
            typeof(User),
            typeof(UserWorkspaceAccess),
            typeof(UserSession),
            typeof(ApiClient),
            typeof(WebhookEndpoint),
            typeof(WebhookSubscription),
            typeof(WebhookDeliveryLog),
            typeof(AuditLog)
        };

        foreach (var mappedType in mappedTypes)
        {
            Assert.NotNull(model.FindEntityType(mappedType));
        }
    }

    [Fact]
    public void Model_UsesSnakeCaseTableAndColumnNames()
    {
        var model = BuildModel();

        foreach (var entityType in model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();

            if (tableName is null)
            {
                continue;
            }

            Assert.Matches(SnakeCaseNamePattern(), tableName);

            var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(table);

                if (columnName is not null)
                {
                    Assert.Matches(SnakeCaseNamePattern(), columnName);
                }
            }
        }
    }

    [Theory]
    [InlineData(typeof(Workspace))]
    [InlineData(typeof(Template))]
    [InlineData(typeof(TemplateVersion))]
    [InlineData(typeof(ContentItem))]
    [InlineData(typeof(MediaAsset))]
    [InlineData(typeof(Tag))]
    [InlineData(typeof(User))]
    [InlineData(typeof(ApiClient))]
    [InlineData(typeof(WebhookEndpoint))]
    public void MutableSoftDeletedEntities_HaveQueryFiltersAndXminConcurrency(Type entityType)
    {
        var mappedEntity = GetEntityType(entityType);

        Assert.NotEmpty(mappedEntity.GetDeclaredQueryFilters());

        var xmin = mappedEntity.FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.True(xmin.IsConcurrencyToken);
        Assert.Equal("xid", xmin.GetColumnType());
    }

    [Theory]
    [InlineData(typeof(TemplateField), nameof(TemplateField.FieldConfig))]
    [InlineData(typeof(ContentFieldValue), nameof(ContentFieldValue.JsonValue))]
    [InlineData(typeof(WebhookDeliveryLog), nameof(WebhookDeliveryLog.Payload))]
    [InlineData(typeof(AuditLog), nameof(AuditLog.ChangeDelta))]
    public void JsonBackedProperties_AreMappedAsJsonb(Type entityType, string propertyName)
    {
        var property = GetEntityType(entityType).FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal("jsonb", property.GetColumnType());
    }

    [Fact]
    public void ContentSearchVector_IsMappedAsTsvectorWithIndex()
    {
        var contentItem = GetEntityType(typeof(ContentItem));
        var searchVector = contentItem.FindProperty(nameof(ContentItem.SearchVector));

        Assert.NotNull(searchVector);
        Assert.Equal("tsvector", searchVector.GetColumnType());
        Assert.Contains(contentItem.GetIndexes(), index => index.Properties.Contains(searchVector));
    }

    [Fact]
    public void TemplateVersion_HasFilteredUniqueDraftIndex()
    {
        var templateVersion = GetEntityType(typeof(TemplateVersion));

        Assert.Contains(templateVersion.GetIndexes(), index =>
            index.IsUnique
            && index.GetFilter() == "status = 'Draft' AND is_deleted = false"
            && index.GetDatabaseName() == "ix_template_versions_one_draft_per_template");
    }

    [Fact]
    public void ContentItemSlug_IndexIgnoresDeletedRowsAndNullSlugs()
    {
        var contentItem = GetEntityType(typeof(ContentItem));

        Assert.Contains(contentItem.GetIndexes(), index =>
            index.IsUnique
            && index.GetFilter() == "slug IS NOT NULL AND is_deleted = false");
    }

    private static IEntityType GetEntityType(Type clrType)
    {
        return BuildModel().FindEntityType(clrType) ?? throw new InvalidOperationException($"Entity type '{clrType.Name}' is not mapped.");
    }

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=cmsify;Username=cmsify;Password=cmsify",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new CmsifyDbContext(options);
        return context.Model;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCaseNamePattern();
}
