namespace Cmsify.Core.Domain.Enums;

public enum PrimitiveType
{
    Text,
    RichText,
    Markdown,
    Boolean,
    PickList,
    Media,
    File,
    Link,
    Quote,
    Separator
}

public enum TemplateVersionStatus
{
    Draft,
    Published,
    Archived
}

public enum CompositionMode
{
    Inline,
    Reference
}

public enum ContentStatus
{
    Draft,
    Review,
    Approved,
    Published,
    Archived
}

public enum ValueKind
{
    Text,
    RichText,
    Markdown,
    Boolean,
    PickList,
    Media,
    File,
    Link,
    Quote,
    Separator,
    ChildContent
}

public enum UserRole
{
    Reader,
    Editor,
    TemplateAdmin,
    Admin
}

public enum AuditAction
{
    Created,
    Updated,
    Deleted,
    StatusChanged
}
