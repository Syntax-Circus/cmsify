namespace Cmsify.Api.Controllers;

/// <summary>
/// Maps public wire contracts at the HTTP boundary without coupling Core repository contracts to the API surface.
/// </summary>
internal static class ContractMappings
{
    public static SyntaxCircus.Cmsify.Contracts.WorkspaceDto ToContract(this Cmsify.Core.Interfaces.Repositories.WorkspaceDto source, bool? canWrite = null) =>
        new(source.Id, source.Name, source.Slug, source.Description, source.CreatedAt, source.UpdatedAt, canWrite ?? source.CanWrite);

    public static SyntaxCircus.Cmsify.Contracts.ApiClientDto ToContract(this Cmsify.Core.Interfaces.Repositories.ApiClientDto source) =>
        new(source.Id, source.Name, source.Description, source.Role.ToContract(), source.WorkspaceId, source.IsActive, source.ExpiresAt, source.CreatedAt, source.LastUsedAt);

    public static SyntaxCircus.Cmsify.Contracts.UserWorkspaceAccessDto ToContract(this Cmsify.Core.Interfaces.Repositories.UserWorkspaceAccessDto source) =>
        new(source.WorkspaceId, source.AccessLevel.ToContract());

    public static SyntaxCircus.Cmsify.Contracts.UserDto ToContract(this Cmsify.Core.Interfaces.Repositories.UserDto source) =>
        new(source.Id, source.Email, source.DisplayName, source.Role.ToContract(), source.IsSuperAdmin, source.MustChangePassword, source.TimeZoneId, source.IsActive, source.CreatedAt, source.LastLoginAt, source.WorkspaceAccesses.Select(ToContract).ToArray());

    public static Cmsify.Core.Domain.Enums.UserRole ToCore(this SyntaxCircus.Cmsify.Contracts.UserRole value) => value switch
    {
        SyntaxCircus.Cmsify.Contracts.UserRole.Reader => Cmsify.Core.Domain.Enums.UserRole.Reader,
        SyntaxCircus.Cmsify.Contracts.UserRole.Editor => Cmsify.Core.Domain.Enums.UserRole.Editor,
        SyntaxCircus.Cmsify.Contracts.UserRole.TemplateAdmin => Cmsify.Core.Domain.Enums.UserRole.TemplateAdmin,
        SyntaxCircus.Cmsify.Contracts.UserRole.Admin => Cmsify.Core.Domain.Enums.UserRole.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.UserRole ToContract(this Cmsify.Core.Domain.Enums.UserRole value) => value switch
    {
        Cmsify.Core.Domain.Enums.UserRole.Reader => SyntaxCircus.Cmsify.Contracts.UserRole.Reader,
        Cmsify.Core.Domain.Enums.UserRole.Editor => SyntaxCircus.Cmsify.Contracts.UserRole.Editor,
        Cmsify.Core.Domain.Enums.UserRole.TemplateAdmin => SyntaxCircus.Cmsify.Contracts.UserRole.TemplateAdmin,
        Cmsify.Core.Domain.Enums.UserRole.Admin => SyntaxCircus.Cmsify.Contracts.UserRole.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static Cmsify.Core.Domain.Enums.WorkspaceAccessLevel ToCore(this SyntaxCircus.Cmsify.Contracts.WorkspaceAccessLevel value) => value switch
    {
        SyntaxCircus.Cmsify.Contracts.WorkspaceAccessLevel.Read => Cmsify.Core.Domain.Enums.WorkspaceAccessLevel.Read,
        SyntaxCircus.Cmsify.Contracts.WorkspaceAccessLevel.Write => Cmsify.Core.Domain.Enums.WorkspaceAccessLevel.Write,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.WorkspaceAccessLevel ToContract(this Cmsify.Core.Domain.Enums.WorkspaceAccessLevel value) => value switch
    {
        Cmsify.Core.Domain.Enums.WorkspaceAccessLevel.Read => SyntaxCircus.Cmsify.Contracts.WorkspaceAccessLevel.Read,
        Cmsify.Core.Domain.Enums.WorkspaceAccessLevel.Write => SyntaxCircus.Cmsify.Contracts.WorkspaceAccessLevel.Write,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static Cmsify.Core.Domain.Enums.ContentStatus ToCore(this SyntaxCircus.Cmsify.Contracts.ContentStatus value) => value switch
    {
        SyntaxCircus.Cmsify.Contracts.ContentStatus.Draft => Cmsify.Core.Domain.Enums.ContentStatus.Draft,
        SyntaxCircus.Cmsify.Contracts.ContentStatus.Review => Cmsify.Core.Domain.Enums.ContentStatus.Review,
        SyntaxCircus.Cmsify.Contracts.ContentStatus.Approved => Cmsify.Core.Domain.Enums.ContentStatus.Approved,
        SyntaxCircus.Cmsify.Contracts.ContentStatus.Published => Cmsify.Core.Domain.Enums.ContentStatus.Published,
        SyntaxCircus.Cmsify.Contracts.ContentStatus.Archived => Cmsify.Core.Domain.Enums.ContentStatus.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.ContentStatus ToContract(this Cmsify.Core.Domain.Enums.ContentStatus value) => value switch
    {
        Cmsify.Core.Domain.Enums.ContentStatus.Draft => SyntaxCircus.Cmsify.Contracts.ContentStatus.Draft,
        Cmsify.Core.Domain.Enums.ContentStatus.Review => SyntaxCircus.Cmsify.Contracts.ContentStatus.Review,
        Cmsify.Core.Domain.Enums.ContentStatus.Approved => SyntaxCircus.Cmsify.Contracts.ContentStatus.Approved,
        Cmsify.Core.Domain.Enums.ContentStatus.Published => SyntaxCircus.Cmsify.Contracts.ContentStatus.Published,
        Cmsify.Core.Domain.Enums.ContentStatus.Archived => SyntaxCircus.Cmsify.Contracts.ContentStatus.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.ContentVersionStatus ToContract(this Cmsify.Core.Domain.Enums.ContentVersionStatus value) => value switch
    {
        Cmsify.Core.Domain.Enums.ContentVersionStatus.Published => SyntaxCircus.Cmsify.Contracts.ContentVersionStatus.Published,
        Cmsify.Core.Domain.Enums.ContentVersionStatus.Retired => SyntaxCircus.Cmsify.Contracts.ContentVersionStatus.Retired,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static Cmsify.Core.Domain.Enums.AuditAction ToCore(this SyntaxCircus.Cmsify.Contracts.AuditAction value) => value switch
    {
        SyntaxCircus.Cmsify.Contracts.AuditAction.Created => Cmsify.Core.Domain.Enums.AuditAction.Created,
        SyntaxCircus.Cmsify.Contracts.AuditAction.Updated => Cmsify.Core.Domain.Enums.AuditAction.Updated,
        SyntaxCircus.Cmsify.Contracts.AuditAction.Deleted => Cmsify.Core.Domain.Enums.AuditAction.Deleted,
        SyntaxCircus.Cmsify.Contracts.AuditAction.StatusChanged => Cmsify.Core.Domain.Enums.AuditAction.StatusChanged,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.AuditAction ToContract(this Cmsify.Core.Domain.Enums.AuditAction value) => value switch
    {
        Cmsify.Core.Domain.Enums.AuditAction.Created => SyntaxCircus.Cmsify.Contracts.AuditAction.Created,
        Cmsify.Core.Domain.Enums.AuditAction.Updated => SyntaxCircus.Cmsify.Contracts.AuditAction.Updated,
        Cmsify.Core.Domain.Enums.AuditAction.Deleted => SyntaxCircus.Cmsify.Contracts.AuditAction.Deleted,
        Cmsify.Core.Domain.Enums.AuditAction.StatusChanged => SyntaxCircus.Cmsify.Contracts.AuditAction.StatusChanged,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static Cmsify.Core.Domain.Enums.PrimitiveType ToCore(this SyntaxCircus.Cmsify.Contracts.PrimitiveType value) =>
        ((SyntaxCircus.Cmsify.Contracts.PrimitiveType?)value).ToCore()!.Value;

    public static Cmsify.Core.Domain.Enums.PrimitiveType? ToCore(this SyntaxCircus.Cmsify.Contracts.PrimitiveType? value) => value switch
    {
        null => null,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.Text => Cmsify.Core.Domain.Enums.PrimitiveType.Text,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.RichText => Cmsify.Core.Domain.Enums.PrimitiveType.RichText,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.Markdown => Cmsify.Core.Domain.Enums.PrimitiveType.Markdown,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.Boolean => Cmsify.Core.Domain.Enums.PrimitiveType.Boolean,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.PickList => Cmsify.Core.Domain.Enums.PrimitiveType.PickList,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.Media => Cmsify.Core.Domain.Enums.PrimitiveType.Media,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.File => Cmsify.Core.Domain.Enums.PrimitiveType.File,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.Link => Cmsify.Core.Domain.Enums.PrimitiveType.Link,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.Quote => Cmsify.Core.Domain.Enums.PrimitiveType.Quote,
        SyntaxCircus.Cmsify.Contracts.PrimitiveType.Separator => Cmsify.Core.Domain.Enums.PrimitiveType.Separator,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.PrimitiveType? ToContract(this Cmsify.Core.Domain.Enums.PrimitiveType? value) => value switch
    {
        null => null,
        Cmsify.Core.Domain.Enums.PrimitiveType.Text => SyntaxCircus.Cmsify.Contracts.PrimitiveType.Text,
        Cmsify.Core.Domain.Enums.PrimitiveType.RichText => SyntaxCircus.Cmsify.Contracts.PrimitiveType.RichText,
        Cmsify.Core.Domain.Enums.PrimitiveType.Markdown => SyntaxCircus.Cmsify.Contracts.PrimitiveType.Markdown,
        Cmsify.Core.Domain.Enums.PrimitiveType.Boolean => SyntaxCircus.Cmsify.Contracts.PrimitiveType.Boolean,
        Cmsify.Core.Domain.Enums.PrimitiveType.PickList => SyntaxCircus.Cmsify.Contracts.PrimitiveType.PickList,
        Cmsify.Core.Domain.Enums.PrimitiveType.Media => SyntaxCircus.Cmsify.Contracts.PrimitiveType.Media,
        Cmsify.Core.Domain.Enums.PrimitiveType.File => SyntaxCircus.Cmsify.Contracts.PrimitiveType.File,
        Cmsify.Core.Domain.Enums.PrimitiveType.Link => SyntaxCircus.Cmsify.Contracts.PrimitiveType.Link,
        Cmsify.Core.Domain.Enums.PrimitiveType.Quote => SyntaxCircus.Cmsify.Contracts.PrimitiveType.Quote,
        Cmsify.Core.Domain.Enums.PrimitiveType.Separator => SyntaxCircus.Cmsify.Contracts.PrimitiveType.Separator,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static Cmsify.Core.Domain.Enums.CompositionMode ToCore(this SyntaxCircus.Cmsify.Contracts.CompositionMode value) => value switch
    {
        SyntaxCircus.Cmsify.Contracts.CompositionMode.Inline => Cmsify.Core.Domain.Enums.CompositionMode.Inline,
        SyntaxCircus.Cmsify.Contracts.CompositionMode.Reference => Cmsify.Core.Domain.Enums.CompositionMode.Reference,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.CompositionMode ToContract(this Cmsify.Core.Domain.Enums.CompositionMode value) => value switch
    {
        Cmsify.Core.Domain.Enums.CompositionMode.Inline => SyntaxCircus.Cmsify.Contracts.CompositionMode.Inline,
        Cmsify.Core.Domain.Enums.CompositionMode.Reference => SyntaxCircus.Cmsify.Contracts.CompositionMode.Reference,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static Cmsify.Core.Domain.Enums.TemplateVersionStatus ToCore(this SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus value) => value switch
    {
        SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus.Draft => Cmsify.Core.Domain.Enums.TemplateVersionStatus.Draft,
        SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus.Published => Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published,
        SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus.Archived => Cmsify.Core.Domain.Enums.TemplateVersionStatus.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus ToContract(this Cmsify.Core.Domain.Enums.TemplateVersionStatus value) => value switch
    {
        Cmsify.Core.Domain.Enums.TemplateVersionStatus.Draft => SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus.Draft,
        Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published => SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus.Published,
        Cmsify.Core.Domain.Enums.TemplateVersionStatus.Archived => SyntaxCircus.Cmsify.Contracts.TemplateVersionStatus.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static Cmsify.Core.Domain.Enums.ValueKind ToCore(this SyntaxCircus.Cmsify.Contracts.ValueKind value) => value switch
    {
        SyntaxCircus.Cmsify.Contracts.ValueKind.Text => Cmsify.Core.Domain.Enums.ValueKind.Text,
        SyntaxCircus.Cmsify.Contracts.ValueKind.RichText => Cmsify.Core.Domain.Enums.ValueKind.RichText,
        SyntaxCircus.Cmsify.Contracts.ValueKind.Markdown => Cmsify.Core.Domain.Enums.ValueKind.Markdown,
        SyntaxCircus.Cmsify.Contracts.ValueKind.Boolean => Cmsify.Core.Domain.Enums.ValueKind.Boolean,
        SyntaxCircus.Cmsify.Contracts.ValueKind.PickList => Cmsify.Core.Domain.Enums.ValueKind.PickList,
        SyntaxCircus.Cmsify.Contracts.ValueKind.Media => Cmsify.Core.Domain.Enums.ValueKind.Media,
        SyntaxCircus.Cmsify.Contracts.ValueKind.File => Cmsify.Core.Domain.Enums.ValueKind.File,
        SyntaxCircus.Cmsify.Contracts.ValueKind.Link => Cmsify.Core.Domain.Enums.ValueKind.Link,
        SyntaxCircus.Cmsify.Contracts.ValueKind.Quote => Cmsify.Core.Domain.Enums.ValueKind.Quote,
        SyntaxCircus.Cmsify.Contracts.ValueKind.Separator => Cmsify.Core.Domain.Enums.ValueKind.Separator,
        SyntaxCircus.Cmsify.Contracts.ValueKind.ChildContent => Cmsify.Core.Domain.Enums.ValueKind.ChildContent,
        SyntaxCircus.Cmsify.Contracts.ValueKind.Component => Cmsify.Core.Domain.Enums.ValueKind.Component,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static SyntaxCircus.Cmsify.Contracts.ValueKind ToContract(this Cmsify.Core.Domain.Enums.ValueKind value) => value switch
    {
        Cmsify.Core.Domain.Enums.ValueKind.Text => SyntaxCircus.Cmsify.Contracts.ValueKind.Text,
        Cmsify.Core.Domain.Enums.ValueKind.RichText => SyntaxCircus.Cmsify.Contracts.ValueKind.RichText,
        Cmsify.Core.Domain.Enums.ValueKind.Markdown => SyntaxCircus.Cmsify.Contracts.ValueKind.Markdown,
        Cmsify.Core.Domain.Enums.ValueKind.Boolean => SyntaxCircus.Cmsify.Contracts.ValueKind.Boolean,
        Cmsify.Core.Domain.Enums.ValueKind.PickList => SyntaxCircus.Cmsify.Contracts.ValueKind.PickList,
        Cmsify.Core.Domain.Enums.ValueKind.Media => SyntaxCircus.Cmsify.Contracts.ValueKind.Media,
        Cmsify.Core.Domain.Enums.ValueKind.File => SyntaxCircus.Cmsify.Contracts.ValueKind.File,
        Cmsify.Core.Domain.Enums.ValueKind.Link => SyntaxCircus.Cmsify.Contracts.ValueKind.Link,
        Cmsify.Core.Domain.Enums.ValueKind.Quote => SyntaxCircus.Cmsify.Contracts.ValueKind.Quote,
        Cmsify.Core.Domain.Enums.ValueKind.Separator => SyntaxCircus.Cmsify.Contracts.ValueKind.Separator,
        Cmsify.Core.Domain.Enums.ValueKind.ChildContent => SyntaxCircus.Cmsify.Contracts.ValueKind.ChildContent,
        Cmsify.Core.Domain.Enums.ValueKind.Component => SyntaxCircus.Cmsify.Contracts.ValueKind.Component,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
