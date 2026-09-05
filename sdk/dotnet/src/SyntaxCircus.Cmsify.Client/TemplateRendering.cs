using System.Text.RegularExpressions;

namespace SyntaxCircus.Cmsify;

/// <summary>
/// Renders <c>${{name}}</c> placeholder tokens in Cmsify content field text against a
/// caller-supplied variable dictionary. This is purely a client-side string transform -- the
/// Cmsify server has no concept of variables and never sees or stores rendered output. Content
/// authors write literal <c>${{name}}</c> tokens into Text/Markdown fields, and each consuming
/// application decides what values to supply at read time (e.g. from its own configuration).
///
/// A variable name present in <c>variables</c> with a <see langword="null"/> value renders as an
/// empty string -- an explicit "blank this out." A variable name <em>not</em> present in
/// <c>variables</c> at all is left untouched in the output as the literal <c>${{name}}</c> token.
/// This is deliberate: a typo'd variable name (e.g. <c>${{supprtEmail}}</c>) should be visibly
/// wrong on the rendered page, not silently disappear.
/// </summary>
public static class CmsifyTemplateRenderer
{
    private static readonly Regex TokenPattern =
        new(@"\$\{\{\s*([A-Za-z][A-Za-z0-9_.-]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Replaces every recognized <c>${{name}}</c> token in <paramref name="template"/> with the
    /// corresponding value from <paramref name="variables"/>. Tokens whose name is not a key in
    /// <paramref name="variables"/> are left untouched.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        if (variables.Count == 0 || !template.Contains("${{", StringComparison.Ordinal))
        {
            return template;
        }

        return TokenPattern.Replace(template, match =>
            variables.TryGetValue(match.Groups[1].Value, out var value)
                ? value ?? string.Empty
                : match.Value);
    }
}
