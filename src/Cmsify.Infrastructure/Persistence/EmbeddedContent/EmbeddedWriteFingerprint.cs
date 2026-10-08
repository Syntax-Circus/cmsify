using System.Security.Cryptography;
using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.EmbeddedContent;

namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

internal static class EmbeddedWriteFingerprint
{
    internal static string Create(Guid actor, Guid workspace, EmbeddedWriteKind kind, Guid item, Guid template,
        string contract, int? source, ContentVersionRevisionCondition? revision, IReadOnlyList<ContentVersionFieldInput> fields)
    {
        // Bounded inline Text inputs have no JSON or references. Normalize legacy tick conditions.
        var candidate = revision?.Candidate;
        if (candidate > DateTimeOffset.MaxValue.UtcTicks / 10) candidate /= 10;
        var value = JsonSerializer.SerializeToUtf8Bytes(new { actor, workspace, kind, item, template, contract, source,
            revision = candidate, fields = fields.OrderBy(f => f.FieldId).ThenBy(f => f.Order).ToArray() });
        return Convert.ToHexStringLower(SHA256.HashData(value));
    }
}
