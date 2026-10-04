using Cmsify.Core.ContentQueries;
using Microsoft.Data.Sqlite;

namespace Cmsify.Infrastructure.Sqlite.Persistence.ContentQueries;

/// <summary>Fixed SQL shapes; every request value is a native provider parameter.</summary>
internal sealed class SqliteContentListSql
{
    private readonly List<object> _parameters = [];
    internal object[] Parameters => _parameters.ToArray();
    private string Parameter(object value)
    {
        var name = "cq" + _parameters.Count;
        _parameters.Add(new SqliteParameter(name, value switch { Guid id => id.ToString().ToUpperInvariant(), DateTimeOffset time => time.UtcTicks, _ => value }));
        return "@" + name;
    }
    internal string Items(ListContentRequest request)
    {
        var predicates = new List<string> { "c.is_deleted = 0", "c.workspace_id = " + Parameter(request.WorkspaceId) };
        Identity(predicates, request, "c");
        if (request.Status is { } status)
            predicates.Add("EXISTS (SELECT 1 FROM content_versions v WHERE v.content_item_id = c.id AND v.status = " + Parameter(status.ToString()) + ")");
        if (request.PublishedAfter is { } after)
            predicates.Add("EXISTS (SELECT 1 FROM content_versions v WHERE v.content_item_id = c.id AND v.published_at >= " + Parameter(after) + ")");
        if (request.PublishedBefore is { } before)
            predicates.Add("EXISTS (SELECT 1 FROM content_versions v WHERE v.content_item_id = c.id AND v.published_at <= " + Parameter(before) + ")");
        foreach (var tag in Tags(request.Tags))
            predicates.Add("EXISTS (SELECT 1 FROM content_item_tags link JOIN tags t ON t.id = link.tag_id AND t.is_deleted = 0 WHERE link.content_item_id = c.id AND t.name = " + Parameter(tag) + ")");
        if (request.CreatedAfter is { } createdAfter) predicates.Add("c.created_at >= " + Parameter(createdAfter));
        if (request.CreatedBefore is { } createdBefore) predicates.Add("c.created_at <= " + Parameter(createdBefore));
        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var pattern = Parameter("%" + request.Q + "%");
            predicates.Add("(COALESCE(c.slug, '') LIKE " + pattern + " OR EXISTS (SELECT 1 FROM content_versions v JOIN content_version_field_values f ON f.content_version_id = v.id WHERE v.content_item_id = c.id AND f.text_value LIKE " + pattern + "))");
        }
        return "SELECT c.* FROM content_items c WHERE " + string.Join(" AND ", predicates);
    }
    internal string Resolved(ContentListCriteria criteria)
    {
        var request = criteria.Request;
        var predicates = Candidates(criteria.EvaluationTime);
        predicates.Add("v.workspace_id = " + Parameter(request.WorkspaceId));
        Identity(predicates, request, "v");
        if (request.PublishedAfter is { } after) predicates.Add("v.published_at >= " + Parameter(after));
        if (request.PublishedBefore is { } before) predicates.Add("v.published_at <= " + Parameter(before));
        var tags = Tags(request.Tags);
        foreach (var tag in tags)
            predicates.Add("EXISTS (SELECT 1 FROM json_each(v.tags) tag WHERE tag.type = 'text' AND tag.value = " + Parameter(tag) + " COLLATE BINARY)");
        var winners = Rank(predicates, resolved: true);
        if (!string.IsNullOrWhiteSpace(request.Q))
            winners += " AND COALESCE(w.slug, '') LIKE " + Parameter("%" + Escape(request.Q) + "%") + " ESCAPE '\\'";
        return winners;
    }
    internal string Serving(DateTimeOffset at, Guid[] ids)
    {
        var predicates = Candidates(at);
        predicates.Add(ids.Length == 0 ? "0 = 1" : "v.content_item_id IN (" + string.Join(", ", ids.Select(id => Parameter(id))) + ")");
        return Rank(predicates, resolved: false);
    }
    private List<string> Candidates(DateTimeOffset at)
    {
        var time = Parameter(at);
        return ["v.status = 'Published'", "EXISTS (SELECT 1 FROM content_items owner WHERE owner.id = v.content_item_id AND owner.is_deleted = 0)",
            "((v.effective_start_at IS NULL AND v.effective_end_at IS NULL) OR (v.effective_start_at <= " + time + " AND " + time + " < v.effective_end_at))"];
    }
    // Preserve the released mode difference: resolved PostgreSQL SQL chose null
    // publication first, whereas ordinary .NET serving selection chose it last.
    private static string Rank(List<string> predicates, bool resolved) =>
        "SELECT w.* FROM content_versions w JOIN (SELECT v.id, ROW_NUMBER() OVER (PARTITION BY v.content_item_id ORDER BY " +
        "CASE WHEN v.effective_start_at IS NOT NULL AND v.effective_end_at IS NOT NULL THEN 0 ELSE 1 END, " +
        "(v.effective_end_at - v.effective_start_at), v.published_at DESC NULLS " + (resolved ? "FIRST" : "LAST") + ", v.version_number DESC) AS winner_rank " +
        "FROM content_versions v WHERE " + string.Join(" AND ", predicates) + ") ranked ON ranked.id = w.id WHERE ranked.winner_rank = 1";
    private void Identity(List<string> predicates, ListContentRequest request, string alias)
    {
        if (request.TemplateVersionId is { } tv) predicates.Add(alias + ".template_version_id = " + Parameter(tv));
        if (request.TemplateId is { } template)
            predicates.Add("EXISTS (SELECT 1 FROM template_versions tv WHERE tv.id = " + alias + ".template_version_id AND tv.is_deleted = 0 AND tv.template_id = " + Parameter(template) + ")");
        if (!string.IsNullOrWhiteSpace(request.LocaleCode)) predicates.Add(alias + ".locale_code = " + Parameter(request.LocaleCode));
        if (request.TranslationGroupId is { } group) predicates.Add(alias + ".translation_group_id = " + Parameter(group));
        if (!string.IsNullOrWhiteSpace(request.Slug)) predicates.Add(alias + ".slug = " + Parameter(request.Slug));
    }
    private static string[] Tags(string? tags) => string.IsNullOrWhiteSpace(tags) ? [] : tags
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(tag => tag.ToLowerInvariant()).Where(tag => tag.Length != 0).Distinct().ToArray();
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
