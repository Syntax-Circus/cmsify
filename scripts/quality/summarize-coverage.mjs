import { execFileSync } from "node:child_process";
import {
  mkdirSync,
  readFileSync,
  readdirSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

const reportFileName = "coverage.cobertura.xml";

function fail(message) {
  throw new Error(message);
}

function parseArguments(arguments_) {
  const options = new Map();
  for (let index = 0; index < arguments_.length; index += 2) {
    const name = arguments_[index];
    const value = arguments_[index + 1];
    if (!["--input", "--json", "--markdown"].includes(name) || !value || options.has(name)) {
      fail("Usage: summarize-coverage.mjs --input <directory> --json <file> --markdown <file>");
    }
    options.set(name, value);
  }
  for (const name of ["--input", "--json", "--markdown"]) {
    if (!options.has(name)) {
      fail("Usage: summarize-coverage.mjs --input <directory> --json <file> --markdown <file>");
    }
  }
  return {
    input: path.resolve(options.get("--input")),
    json: path.resolve(options.get("--json")),
    markdown: path.resolve(options.get("--markdown")),
  };
}

function findReports(directory) {
  let details;
  try {
    details = statSync(directory);
  } catch {
    fail(`Coverage input must contain at least one ${reportFileName}: ${directory}`);
  }
  if (!details.isDirectory()) {
    fail(`Coverage input must be a directory containing ${reportFileName}: ${directory}`);
  }

  const reports = [];
  function visit(current) {
    const entries = readdirSync(current, { withFileTypes: true })
      .sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
    for (const entry of entries) {
      const entryPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        visit(entryPath);
      } else if (entry.isFile() && entry.name === reportFileName) {
        reports.push(entryPath);
      }
    }
  }
  visit(directory);
  if (reports.length === 0) {
    fail(`Coverage input must contain at least one ${reportFileName}: ${directory}`);
  }
  return reports;
}

function validateXmlStructure(xml, reportPath) {
  const stack = [];
  const tagPattern = /<!--[\s\S]*?-->|<\?[\s\S]*?\?>|<!\[CDATA\[[\s\S]*?\]\]>|<![^>]*>|<\/?([A-Za-z_][\w:.-]*)(?:\s[^<>]*?)?\s*\/?>/g;
  let match;
  let consumed = 0;
  while ((match = tagPattern.exec(xml)) !== null) {
    const betweenTags = xml.slice(consumed, match.index);
    if (betweenTags.includes("<") || betweenTags.includes(">")) {
      fail(`Malformed Cobertura report: ${reportPath}`);
    }
    consumed = tagPattern.lastIndex;
    const token = match[0];
    if (token.startsWith("<?") || token.startsWith("<!")) continue;
    const name = match[1];
    if (token.startsWith("</")) {
      if (stack.pop() !== name) fail(`Malformed Cobertura report: ${reportPath}`);
    } else if (!token.endsWith("/>")) {
      stack.push(name);
    }
  }
  if (stack.length !== 0 || xml.slice(consumed).includes("<") || xml.slice(consumed).includes(">")) {
    fail(`Malformed Cobertura report: ${reportPath}`);
  }
  if (!/^\s*(?:<\?xml[\s\S]*?\?>\s*)?<coverage\b/i.test(xml) || !/<\/coverage>\s*$/i.test(xml)) {
    fail(`Malformed Cobertura report: ${reportPath}`);
  }
}

function decodeXml(value, reportPath) {
  return value.replace(/&(#(?:x[0-9a-f]+|\d+)|amp|apos|gt|lt|quot);/gi, (entity, name) => {
    const lowerName = name.toLowerCase();
    if (lowerName === "amp") return "&";
    if (lowerName === "apos") return "'";
    if (lowerName === "gt") return ">";
    if (lowerName === "lt") return "<";
    if (lowerName === "quot") return '"';
    const codePoint = lowerName.startsWith("#x")
      ? Number.parseInt(lowerName.slice(2), 16)
      : Number.parseInt(lowerName.slice(1), 10);
    try {
      return String.fromCodePoint(codePoint);
    } catch {
      fail(`Malformed Cobertura report: ${reportPath}`);
    }
  });
}

function parseAttributes(source, reportPath) {
  const attributes = new Map();
  const pattern = /([A-Za-z_][\w:.-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/g;
  let match;
  let consumed = "";
  while ((match = pattern.exec(source)) !== null) {
    consumed += source.slice(consumed.length, match.index).replace(/\s+/g, "");
    if (consumed !== "") fail(`Malformed Cobertura report: ${reportPath}`);
    const name = match[1];
    if (attributes.has(name)) fail(`Malformed Cobertura report: ${reportPath}`);
    attributes.set(name, decodeXml(match[2] ?? match[3], reportPath));
    source = source.slice(pattern.lastIndex);
    pattern.lastIndex = 0;
    consumed = "";
  }
  if (source.trim() !== "") fail(`Malformed Cobertura report: ${reportPath}`);
  return attributes;
}

function nonNegativeInteger(value, reportPath) {
  if (!/^\d+$/.test(value ?? "")) fail(`Malformed Cobertura report: ${reportPath}`);
  return Number.parseInt(value, 10);
}

function parsePackage(packageAttributes, packageBody, reportPath) {
  const attributes = parseAttributes(packageAttributes, reportPath);
  const assembly = attributes.get("name");
  if (!assembly) fail(`Malformed Cobertura report: ${reportPath}`);

  const classLevelBody = packageBody.replace(/<methods\b[^>]*>[\s\S]*?<\/methods>/gi, "");
  const counts = {
    assembly,
    lines: { valid: 0, covered: 0 },
    branches: { valid: 0, covered: 0 },
  };
  const linePattern = /<line\b([^<>]*?)(?:\/\s*>|>[\s\S]*?<\/line>)/gi;
  let lineMatch;
  while ((lineMatch = linePattern.exec(classLevelBody)) !== null) {
    const line = parseAttributes(lineMatch[1], reportPath);
    const hits = nonNegativeInteger(line.get("hits"), reportPath);
    counts.lines.valid += 1;
    if (hits > 0) counts.lines.covered += 1;

    if ((line.get("branch") ?? "false").toLowerCase() === "true") {
      const branchCounts = /\(\s*(\d+)\s*\/\s*(\d+)\s*\)/.exec(line.get("condition-coverage") ?? "");
      if (!branchCounts) fail(`Malformed Cobertura report: ${reportPath}`);
      const covered = nonNegativeInteger(branchCounts[1], reportPath);
      const valid = nonNegativeInteger(branchCounts[2], reportPath);
      if (covered > valid) fail(`Malformed Cobertura report: ${reportPath}`);
      counts.branches.covered += covered;
      counts.branches.valid += valid;
    }
  }
  return counts;
}

function parseReport(reportPath) {
  const xml = readFileSync(reportPath, "utf8");
  validateXmlStructure(xml, reportPath);
  const packages = [];
  const packagePattern = /<package\b([^<>]*)>([\s\S]*?)<\/package>/gi;
  let match;
  while ((match = packagePattern.exec(xml)) !== null) {
    packages.push(parsePackage(match[1], match[2], reportPath));
  }
  if (packages.length === 0) fail(`Malformed Cobertura report: ${reportPath}`);
  return packages;
}

function percentage(covered, valid) {
  return valid === 0 ? 100 : Math.round((covered * 10_000) / valid) / 100;
}

function resolveSourceSha() {
  const environmentSha = process.env.GITHUB_SHA?.trim();
  const sourceSha = environmentSha || execFileSync("git", ["rev-parse", "HEAD"], {
    encoding: "utf8",
  }).trim();
  if (!/^[0-9a-f]{40}$/i.test(sourceSha)) {
    fail("Coverage summary requires a full 40-character source SHA.");
  }
  return sourceSha.toLowerCase();
}

function renderJson(sourceSha, assemblies) {
  return `${JSON.stringify({ schema: "cmsify.coverage.v1", sourceSha, assemblies }, null, 2)}\n`;
}

function escapeMarkdown(value) {
  return value.replaceAll("\\", "\\\\").replaceAll("|", "\\|");
}

function renderMarkdown(sourceSha, assemblies) {
  const rows = assemblies.map((entry) =>
    `| ${escapeMarkdown(entry.assembly)} | ${entry.lines.covered} / ${entry.lines.valid} | ${entry.lines.percentage.toFixed(2)}% | ${entry.branches.covered} / ${entry.branches.valid} | ${entry.branches.percentage.toFixed(2)}% |`);
  return [
    "# Coverage trend",
    "",
    `Source SHA: \`${sourceSha}\``,
    "",
    "| Assembly | Lines | Line coverage | Branches | Branch coverage |",
    "| --- | ---: | ---: | ---: | ---: |",
    ...rows,
    "",
  ].join("\n");
}

function writeSummary(filePath, contents) {
  mkdirSync(path.dirname(filePath), { recursive: true });
  writeFileSync(filePath, contents, "utf8");
}

try {
  const options = parseArguments(process.argv.slice(2));
  const grouped = new Map();
  for (const report of findReports(options.input)) {
    for (const entry of parseReport(report)) {
      const current = grouped.get(entry.assembly) ?? {
        assembly: entry.assembly,
        lines: { valid: 0, covered: 0 },
        branches: { valid: 0, covered: 0 },
      };
      current.lines.valid += entry.lines.valid;
      current.lines.covered += entry.lines.covered;
      current.branches.valid += entry.branches.valid;
      current.branches.covered += entry.branches.covered;
      grouped.set(entry.assembly, current);
    }
  }

  const assemblies = [...grouped.values()]
    .sort((left, right) => left.assembly < right.assembly ? -1 : left.assembly > right.assembly ? 1 : 0)
    .map((entry) => ({
      assembly: entry.assembly,
      lines: { ...entry.lines, percentage: percentage(entry.lines.covered, entry.lines.valid) },
      branches: { ...entry.branches, percentage: percentage(entry.branches.covered, entry.branches.valid) },
    }));
  const sourceSha = resolveSourceSha();
  writeSummary(options.json, renderJson(sourceSha, assemblies));
  writeSummary(options.markdown, renderMarkdown(sourceSha, assemblies));
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
