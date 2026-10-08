using Cmsify.Core.EmbeddedContent;

namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

public sealed class EmbeddedContentReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public EmbeddedWriteKind Kind { get; set; }
    public Guid OperationKey { get; set; }
    public Guid ContentItemId { get; set; }
    public Guid ContentVersionId { get; set; }
    public int VersionNumber { get; set; }
    public Guid TemplateVersionId { get; set; }
    public required string ContractFingerprint { get; set; }
    public required string InputFingerprint { get; set; }
    public long CommittedRevision { get; set; }
    public Guid ActorSubject { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
}
