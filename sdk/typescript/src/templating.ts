/**
 * Renders `${{name}}` placeholder tokens in Cmsify content field text against a caller-supplied
 * variable dictionary. This is purely a client-side string transform -- the Cmsify server has no
 * concept of variables and never sees or stores rendered output. Content authors write literal
 * `${{name}}` tokens into Text/Markdown fields, and each consuming application decides what
 * values to supply at read time (e.g. from its own configuration).
 *
 * A variable name present in `variables` with a `null`/`undefined` value renders as an empty
 * string -- an explicit "blank this out." A variable name *not* present in `variables` at all is
 * left untouched in the output as the literal `${{name}}` token. This is deliberate: a typo'd
 * variable name (e.g. `${{supprtEmail}}`) should be visibly wrong on the rendered page, not
 * silently disappear.
 */

const TOKEN_PATTERN = /\$\{\{\s*([A-Za-z][A-Za-z0-9_.-]*)\s*\}\}/g;

/**
 * Replaces every recognized `${{name}}` token in `template` with the corresponding value from
 * `variables`. Tokens whose name is not a key in `variables` are left untouched.
 */
export function renderCmsifyTemplate(
  template: string,
  variables: Record<string, string | null | undefined>,
): string {
  if (!template.includes("${{")) {
    return template;
  }

  return template.replace(TOKEN_PATTERN, (full, name: string) =>
    Object.prototype.hasOwnProperty.call(variables, name) ? (variables[name] ?? "") : full,
  );
}
