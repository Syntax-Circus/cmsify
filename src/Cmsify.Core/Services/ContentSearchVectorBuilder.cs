using System.Text;
using System.Text.RegularExpressions;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Core.Services;

public sealed partial class ContentSearchVectorBuilder : IContentSearchVectorBuilder
{
    // PostgreSQL's tsvector limits. A value beyond any of them is rejected, which fails the whole content save.
    private const int MaxPosition = 16383;
    private const int MaxPositionsPerTerm = 256;
    private const int MaxTermBytes = 2046;
    private const int MaxVectorBytes = (1 << 20) - 1;

    public string Build(ContentVersion version, TemplateVersion templateVersion)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(templateVersion);

        var searchableFieldIds = templateVersion.Fields
            .Where(field => field.PrimitiveType is PrimitiveType.Text or PrimitiveType.RichText or PrimitiveType.Markdown or PrimitiveType.PickList or PrimitiveType.Link or PrimitiveType.Quote)
            .Where(field => field.PrimitiveType != PrimitiveType.Text || TextFormatHints.IsSearchIndexable(TextFormatHints.GetEffectiveHint(field.FieldConfig)))
            .Select(field => field.Id)
            .ToHashSet();

        var text = string.Join(' ', version.FieldValues
            .Where(value => searchableFieldIds.Contains(value.FieldId) && !string.IsNullOrWhiteSpace(value.TextValue))
            .OrderBy(value => value.Order)
            .Select(value => value.TextValue));

        if (!string.IsNullOrWhiteSpace(version.Slug))
        {
            text = $"{version.Slug} {text}";
        }

        var terms = TokenRegex().Matches(text.ToLowerInvariant())
            .Select((match, index) => (Term: match.Value, Position: Math.Min(index + 1, MaxPosition)))
            .GroupBy(term => term.Term, term => term.Position);

        return string.Join(' ', WithinLimits(terms));
    }

    // Keeps the vector within PostgreSQL's limits. Terms arrive in order of first appearance, so when the
    // total size runs out, the earliest ones are kept.
    private static IEnumerable<string> WithinLimits(IEnumerable<IGrouping<string, int>> terms)
    {
        var vectorBytes = 0;
        foreach (var term in terms)
        {
            var termBytes = Encoding.UTF8.GetByteCount(term.Key);
            if (termBytes > MaxTermBytes)
            {
                continue;
            }

            // Positions past MaxPosition all share its value, so they must be de-duplicated.
            var positions = term.Distinct().Take(MaxPositionsPerTerm).ToArray();

            // The server stores the term, up to one padding byte, a 2-byte count and 2 bytes per position.
            var storedBytes = termBytes + 1 + sizeof(ushort) * (1 + positions.Length);
            if (vectorBytes + storedBytes > MaxVectorBytes)
            {
                yield break;
            }

            vectorBytes += storedBytes;
            yield return $"'{term.Key.Replace("'", "''", StringComparison.Ordinal)}':{string.Join(",", positions)}";
        }
    }

    [GeneratedRegex("[\\p{L}\\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();
}
