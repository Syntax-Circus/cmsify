using System.Text.RegularExpressions;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Core.Services;

public sealed partial class ContentSearchVectorBuilder : IContentSearchVectorBuilder
{
    public string Build(ContentItem item, TemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(version);

        var searchableFieldIds = version.Fields
            .Where(field => field.PrimitiveType is PrimitiveType.Text or PrimitiveType.RichText or PrimitiveType.Markdown or PrimitiveType.PickList or PrimitiveType.Link or PrimitiveType.Quote)
            .Select(field => field.Id)
            .ToHashSet();

        var text = string.Join(' ', item.FieldValues
            .Where(value => searchableFieldIds.Contains(value.FieldId) && !string.IsNullOrWhiteSpace(value.TextValue))
            .OrderBy(value => value.Order)
            .Select(value => value.TextValue));

        if (!string.IsNullOrWhiteSpace(item.Slug))
        {
            text = $"{item.Slug} {text}";
        }

        var terms = TokenRegex().Matches(text.ToLowerInvariant())
            .Select((match, index) => (Term: match.Value.Replace("'", "''", StringComparison.Ordinal), Position: index + 1))
            .GroupBy(term => term.Term)
            .Select(group => $"'{group.Key}':{string.Join(",", group.Select(term => term.Position))}");

        return string.Join(' ', terms);
    }

    [GeneratedRegex("[\\p{L}\\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();
}
