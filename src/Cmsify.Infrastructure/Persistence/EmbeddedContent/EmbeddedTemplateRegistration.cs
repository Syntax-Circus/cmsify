namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

public sealed class EmbeddedTemplateRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string ContractKey { get; set; }
    public required string Fingerprint { get; set; }
    public Guid TemplateId { get; set; }
    public Guid TemplateVersionId { get; set; }
}
