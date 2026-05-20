/**
 * Text formatting hint metadata for Cmsify text fields.
 *
 * Surfaced via the `formatHint` property on a Text field's `fieldConfig`. Consumers can use
 * this to choose an appropriate renderer (syntax highlighter, sanitizer, etc).
 *
 * The hint is advisory: a consumer that does not recognize a value should fall back to
 * plain-text rendering.
 */
export type TextFormatHint =
  | "plaintext"
  | "html"
  | "markdown"
  | "json"
  | "xml"
  | "yaml"
  | "csv"
  | "toml"
  | "sql"
  | "code"
  | "url"
  | "email"
  | "regex";

export const TEXT_FORMAT_HINTS: readonly TextFormatHint[] = [
  "plaintext",
  "html",
  "markdown",
  "json",
  "xml",
  "yaml",
  "csv",
  "toml",
  "sql",
  "code",
  "url",
  "email",
  "regex",
];

/**
 * Field config shape for the formatting-clue feature. Lives inside a Text field's
 * `fieldConfig`. All properties are optional; absence implies plain text.
 */
export interface TextFormatHintConfig {
  formatHint?: TextFormatHint;
  /** Only meaningful when `formatHint === "code"`. e.g. "typescript", "python". */
  formatLanguage?: string;
  /** When true, the server will syntactically validate values against the hint on save. */
  validateFormat?: boolean;
}

const MIME_BY_HINT: Record<TextFormatHint, string> = {
  plaintext: "text/plain",
  html: "text/html",
  markdown: "text/markdown",
  json: "application/json",
  xml: "application/xml",
  yaml: "application/yaml",
  csv: "text/csv",
  toml: "application/toml",
  sql: "application/sql",
  code: "text/plain",
  url: "text/uri-list",
  email: "text/plain",
  regex: "text/plain",
};

/**
 * Returns the IANA-ish media type a consumer should use when handling a value with the
 * given hint. Defaults to `text/plain` for unknown/unspecified hints.
 */
export function toMimeType(hint: TextFormatHint | string | undefined | null): string {
  if (hint && hint in MIME_BY_HINT) {
    return MIME_BY_HINT[hint as TextFormatHint];
  }

  return "text/plain";
}

/**
 * Reads the effective format hint from a field config object. Returns `"plaintext"` when
 * the config is missing, malformed, or specifies an unknown hint (forward-compatible).
 */
export function getFormatHint(fieldConfig: unknown): TextFormatHint {
  if (!fieldConfig || typeof fieldConfig !== "object") {
    return "plaintext";
  }

  const raw = (fieldConfig as Record<string, unknown>).formatHint;
  if (typeof raw !== "string") {
    return "plaintext";
  }

  const normalized = raw.toLowerCase() as TextFormatHint;
  return TEXT_FORMAT_HINTS.includes(normalized) ? normalized : "plaintext";
}

/**
 * Hints that represent natural-language content and may be safe to render as prose.
 * Useful for consumers deciding between a rich renderer and a code/monospace view.
 */
export function isProseHint(hint: TextFormatHint): boolean {
  return hint === "plaintext" || hint === "markdown" || hint === "html";
}
