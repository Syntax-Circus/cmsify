import { randomUUID } from "node:crypto";
import { execFileSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

const reportFileName = "coverage.cobertura.xml";
const nameStart = /[A-Za-z_:]/;
const nameCharacter = /[A-Za-z0-9_.:-]/;

function fail(message) {
  throw new Error(message);
}

function malformed(reportPath) {
  fail(`Malformed Cobertura report: ${reportPath}`);
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

function pathIdentity(filePath) {
  const absolute = path.resolve(filePath);
  const suffix = [];
  let existing = absolute;
  while (!existsSync(existing)) {
    const parent = path.dirname(existing);
    if (parent === existing) fail(`Cannot resolve output path: ${filePath}`);
    suffix.unshift(path.basename(existing));
    existing = parent;
  }
  const canonical = path.join(realpathSync.native(existing), ...suffix);
  const normalized = process.platform === "win32" ? canonical.toLowerCase() : canonical;
  const details = existsSync(absolute) ? statSync(absolute) : null;
  return {
    canonical: normalized,
    physical: details ? `${details.dev}:${details.ino}` : null,
  };
}

function sameIdentity(left, right) {
  return left.canonical === right.canonical
    || (left.physical !== null && right.physical !== null && left.physical === right.physical);
}

function validateOutputPaths(options, reports) {
  const jsonIdentity = pathIdentity(options.json);
  const markdownIdentity = pathIdentity(options.markdown);
  if (sameIdentity(jsonIdentity, markdownIdentity)) {
    fail("Coverage JSON and Markdown output paths must be distinct.");
  }

  const reportIdentities = reports.map(pathIdentity);
  for (const [label, outputPath, identity] of [
    ["JSON", options.json, jsonIdentity],
    ["Markdown", options.markdown, markdownIdentity],
  ]) {
    if (reportIdentities.some((report) => sameIdentity(identity, report))) {
      fail(`Coverage ${label} output must not alias a raw coverage report: ${outputPath}`);
    }
    if (existsSync(outputPath) && !statSync(outputPath).isFile()) {
      fail(`Coverage ${label} output destination must be a file: ${outputPath}`);
    }
  }
}

function isXmlCodePoint(codePoint) {
  return codePoint === 0x9
    || codePoint === 0xA
    || codePoint === 0xD
    || (codePoint >= 0x20 && codePoint <= 0xD7FF)
    || (codePoint >= 0xE000 && codePoint <= 0xFFFD)
    || (codePoint >= 0x10000 && codePoint <= 0x10FFFF);
}

function validateCharacters(value, reportPath) {
  for (const character of value) {
    if (!isXmlCodePoint(character.codePointAt(0))) malformed(reportPath);
  }
}

function decodeEntities(value, reportPath) {
  validateCharacters(value, reportPath);
  let decoded = "";
  for (let index = 0; index < value.length;) {
    if (value[index] !== "&") {
      decoded += value[index];
      index += 1;
      continue;
    }
    const end = value.indexOf(";", index + 1);
    if (end < 0) malformed(reportPath);
    const entity = value.slice(index + 1, end);
    const predefined = new Map([
      ["amp", "&"],
      ["apos", "'"],
      ["gt", ">"],
      ["lt", "<"],
      ["quot", '"'],
    ]);
    if (predefined.has(entity)) {
      decoded += predefined.get(entity);
    } else {
      const decimal = /^#([0-9]+)$/.exec(entity);
      const hexadecimal = /^#x([0-9A-Fa-f]+)$/.exec(entity);
      if (!decimal && !hexadecimal) malformed(reportPath);
      const codePoint = Number.parseInt(decimal?.[1] ?? hexadecimal[1], decimal ? 10 : 16);
      if (!isXmlCodePoint(codePoint)) malformed(reportPath);
      decoded += String.fromCodePoint(codePoint);
    }
    index = end + 1;
  }
  return decoded;
}

function parseXml(xml, reportPath) {
  if (xml.charCodeAt(0) === 0xFEFF) xml = xml.slice(1);
  validateCharacters(xml, reportPath);
  let index = 0;
  let root = null;
  let sawDoctype = false;
  let sawXmlDeclaration = false;
  const stack = [];

  const whitespace = () => {
    while (index < xml.length && /[\t\n\r ]/.test(xml[index])) index += 1;
  };
  const parseName = () => {
    if (!nameStart.test(xml[index] ?? "")) malformed(reportPath);
    const start = index;
    index += 1;
    while (index < xml.length && nameCharacter.test(xml[index])) index += 1;
    return xml.slice(start, index);
  };
  const parseAttributes = (terminators) => {
    const attributes = new Map();
    while (index < xml.length) {
      const separatorStart = index;
      whitespace();
      if (terminators.some((terminator) => xml.startsWith(terminator, index))) return attributes;
      if (index === separatorStart) malformed(reportPath);
      const name = parseName();
      if (attributes.has(name)) malformed(reportPath);
      whitespace();
      if (xml[index] !== "=") malformed(reportPath);
      index += 1;
      whitespace();
      const quote = xml[index];
      if (quote !== '"' && quote !== "'") malformed(reportPath);
      index += 1;
      const start = index;
      while (index < xml.length && xml[index] !== quote) {
        if (xml[index] === "<") malformed(reportPath);
        index += 1;
      }
      if (index >= xml.length) malformed(reportPath);
      attributes.set(name, decodeEntities(xml.slice(start, index), reportPath));
      index += 1;
    }
    malformed(reportPath);
  };

  while (index < xml.length) {
    if (xml[index] !== "<") {
      const end = xml.indexOf("<", index);
      const next = end < 0 ? xml.length : end;
      const text = xml.slice(index, next);
      if (text.includes("]]>") || (stack.length === 0 && text.trim() !== "")) malformed(reportPath);
      decodeEntities(text, reportPath);
      index = next;
      continue;
    }

    if (xml.startsWith("<!--", index)) {
      const end = xml.indexOf("-->", index + 4);
      const comment = end < 0 ? "" : xml.slice(index + 4, end);
      if (end < 0 || comment.includes("--") || comment.endsWith("-")) malformed(reportPath);
      index = end + 3;
      continue;
    }
    if (xml.startsWith("<![CDATA[", index)) {
      if (stack.length === 0) malformed(reportPath);
      const end = xml.indexOf("]]>", index + 9);
      if (end < 0) malformed(reportPath);
      validateCharacters(xml.slice(index + 9, end), reportPath);
      index = end + 3;
      continue;
    }
    if (xml.startsWith("<?", index)) {
      const declarationStart = index;
      index += 2;
      const target = parseName();
      const end = xml.indexOf("?>", index);
      if (end < 0) malformed(reportPath);
      if (index < end && !/[\t\n\r ]/.test(xml[index])) malformed(reportPath);
      if (target.toLowerCase() === "xml") {
        if (target !== "xml" || declarationStart !== 0 || sawXmlDeclaration || sawDoctype || root !== null) {
          malformed(reportPath);
        }
        const declaration = xml.slice(index, end);
        let declarationIndex = 0;
        const declarationAttributes = new Map();
        while (declarationIndex < declaration.length) {
          const separatorStart = declarationIndex;
          while (/[\t\n\r ]/.test(declaration[declarationIndex] ?? "")) declarationIndex += 1;
          if (declarationIndex >= declaration.length) break;
          if (declarationIndex === separatorStart) malformed(reportPath);
          const nameMatch = /^[A-Za-z_:][A-Za-z0-9_.:-]*/.exec(declaration.slice(declarationIndex));
          if (!nameMatch) malformed(reportPath);
          const name = nameMatch[0];
          declarationIndex += name.length;
          while (/[\t\n\r ]/.test(declaration[declarationIndex] ?? "")) declarationIndex += 1;
          if (declaration[declarationIndex] !== "=") malformed(reportPath);
          declarationIndex += 1;
          while (/[\t\n\r ]/.test(declaration[declarationIndex] ?? "")) declarationIndex += 1;
          const quote = declaration[declarationIndex];
          if (quote !== '"' && quote !== "'") malformed(reportPath);
          declarationIndex += 1;
          const valueStart = declarationIndex;
          while (declarationIndex < declaration.length && declaration[declarationIndex] !== quote) declarationIndex += 1;
          if (declarationIndex >= declaration.length || declarationAttributes.has(name)) malformed(reportPath);
          declarationAttributes.set(name, declaration.slice(valueStart, declarationIndex));
          declarationIndex += 1;
        }
        const declarationNames = [...declarationAttributes.keys()];
        const expectedNames = [
          "version",
          ...(declarationAttributes.has("encoding") ? ["encoding"] : []),
          ...(declarationAttributes.has("standalone") ? ["standalone"] : []),
        ];
        if (declarationNames.length !== expectedNames.length
          || declarationNames.some((name, attributeIndex) => name !== expectedNames[attributeIndex])
          || !/^1\.[0-9]+$/.test(declarationAttributes.get("version") ?? "")
          || (declarationAttributes.has("encoding")
            && !/^[A-Za-z][A-Za-z0-9._-]*$/.test(declarationAttributes.get("encoding")))
          || (declarationAttributes.has("standalone")
            && !/^(?:yes|no)$/.test(declarationAttributes.get("standalone")))) {
          malformed(reportPath);
        }
        sawXmlDeclaration = true;
      }
      index = end + 2;
      continue;
    }
    if (xml.startsWith("<!DOCTYPE", index)) {
      if (stack.length !== 0 || root !== null || sawDoctype) malformed(reportPath);
      let end = index + 9;
      let quote = null;
      for (; end < xml.length; end += 1) {
        const character = xml[end];
        if (quote !== null) {
          if (character === quote) quote = null;
        } else if (character === '"' || character === "'") {
          quote = character;
        } else if (character === "[") {
          malformed(reportPath);
        } else if (character === ">") {
          break;
        }
      }
      if (end >= xml.length || quote !== null) malformed(reportPath);
      const declaration = xml.slice(index + 9, end);
      if (!/^\s+[A-Za-z_:][A-Za-z0-9_.:-]*(?:\s+(?:SYSTEM\s+(?:"[^"]*"|'[^']*')|PUBLIC\s+(?:"[^"]*"|'[^']*')\s+(?:"[^"]*"|'[^']*')))?\s*$/.test(declaration)) {
        malformed(reportPath);
      }
      sawDoctype = true;
      index = end + 1;
      continue;
    }
    if (xml.startsWith("<!", index)) malformed(reportPath);

    if (xml.startsWith("</", index)) {
      index += 2;
      const name = parseName();
      whitespace();
      if (xml[index] !== ">" || stack.length === 0 || stack.at(-1).name !== name) malformed(reportPath);
      stack.pop();
      index += 1;
      continue;
    }

    index += 1;
    const name = parseName();
    const attributes = parseAttributes(["/>", ">"]);
    const selfClosing = xml.startsWith("/>", index);
    index += selfClosing ? 2 : 1;
    const element = { name, attributes, children: [] };
    if (stack.length > 0) {
      stack.at(-1).children.push(element);
    } else {
      if (root !== null) malformed(reportPath);
      root = element;
    }
    if (!selfClosing) stack.push(element);
  }

  if (stack.length !== 0 || root?.name !== "coverage") malformed(reportPath);
  return root;
}

function children(element, name) {
  return element.children.filter((child) => child.name === name);
}

function nonNegativeInteger(value, reportPath) {
  if (!/^\d+$/.test(value ?? "")) malformed(reportPath);
  return Number.parseInt(value, 10);
}

function parsePackage(element, reportPath) {
  const assembly = element.attributes.get("name");
  if (!assembly) malformed(reportPath);
  const counts = {
    assembly,
    lines: { valid: 0, covered: 0 },
    branches: { valid: 0, covered: 0 },
  };
  const classElements = children(element, "classes").flatMap((classes) => children(classes, "class"));
  const lineElements = classElements
    .flatMap((classElement) => children(classElement, "lines"))
    .flatMap((lines) => children(lines, "line"));
  for (const line of lineElements) {
    const hits = nonNegativeInteger(line.attributes.get("hits"), reportPath);
    counts.lines.valid += 1;
    if (hits > 0) counts.lines.covered += 1;

    const branch = (line.attributes.get("branch") ?? "false").toLowerCase();
    if (branch !== "true" && branch !== "false") malformed(reportPath);
    if (branch === "true") {
      const branchCounts = /\(\s*(\d+)\s*\/\s*(\d+)\s*\)/.exec(line.attributes.get("condition-coverage") ?? "");
      if (!branchCounts) malformed(reportPath);
      const covered = nonNegativeInteger(branchCounts[1], reportPath);
      const valid = nonNegativeInteger(branchCounts[2], reportPath);
      if (covered > valid) malformed(reportPath);
      counts.branches.covered += covered;
      counts.branches.valid += valid;
    }
  }
  return counts;
}

function parseReport(reportPath) {
  const root = parseXml(readFileSync(reportPath, "utf8"), reportPath);
  const packages = children(root, "packages")
    .flatMap((packageContainer) => children(packageContainer, "package"))
    .map((packageElement) => parsePackage(packageElement, reportPath));
  if (packages.length === 0) malformed(reportPath);
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

function stagedFile(destination, contents) {
  mkdirSync(path.dirname(destination), { recursive: true });
  const temporary = path.join(
    path.dirname(destination),
    `.${path.basename(destination)}.${process.pid}.${randomUUID()}.tmp`,
  );
  writeFileSync(temporary, contents, { encoding: "utf8", flag: "wx" });
  return temporary;
}

function replaceOutputs(outputs) {
  const staged = [];
  const backups = [];
  const installed = [];
  try {
    for (const output of outputs) {
      staged.push({ ...output, temporary: stagedFile(output.destination, output.contents) });
    }
    for (const output of staged) {
      if (existsSync(output.destination)) {
        const backup = `${output.temporary}.previous`;
        renameSync(output.destination, backup);
        backups.push({ backup, destination: output.destination });
      }
    }
    for (const output of staged) {
      renameSync(output.temporary, output.destination);
      installed.push(output.destination);
    }
    for (const { backup } of backups) rmSync(backup, { force: true });
  } catch (error) {
    for (const destination of installed.reverse()) rmSync(destination, { force: true });
    for (const { backup, destination } of backups.reverse()) {
      if (existsSync(backup)) renameSync(backup, destination);
    }
    throw error;
  } finally {
    for (const output of staged) rmSync(output.temporary, { force: true });
    for (const { backup } of backups) rmSync(backup, { force: true });
  }
}

try {
  const options = parseArguments(process.argv.slice(2));
  const reports = findReports(options.input);
  validateOutputPaths(options, reports);
  const grouped = new Map();
  for (const report of reports) {
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
  replaceOutputs([
    { destination: options.json, contents: renderJson(sourceSha, assemblies) },
    { destination: options.markdown, contents: renderMarkdown(sourceSha, assemblies) },
  ]);
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
