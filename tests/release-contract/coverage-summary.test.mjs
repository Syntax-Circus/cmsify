import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const summarizer = path.join(repositoryRoot, "scripts/quality/summarize-coverage.mjs");
const sourceSha = "0123456789abcdef0123456789abcdef01234567";

function cobertura(packages) {
  return `<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0" branch-rate="0" lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="0">
  <packages>
${packages.join("\n")}
  </packages>
</coverage>
`;
}

function assembly(name, lines) {
  return `    <package name="${name}" line-rate="0" branch-rate="0">
      <classes>
        <class name="Example" filename="Example.cs" line-rate="0" branch-rate="0">
          <methods />
          <lines>
${lines.map((line) => `            ${line}`).join("\n")}
          </lines>
        </class>
      </classes>
    </package>`;
}

function run(input, json, markdown, environment = {}) {
  return spawnSync(process.execPath, [
    summarizer,
    "--input", input,
    "--json", json,
    "--markdown", markdown,
  ], {
    cwd: repositoryRoot,
    encoding: "utf8",
    env: { ...process.env, GITHUB_SHA: sourceSha, ...environment },
  });
}

function withTemporaryDirectory(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "cmsify-coverage-summary-"));
  try {
    callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("recursively groups exact Cobertura reports by assembly into stable trend summaries", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    const firstDirectory = path.join(input, "z", "run-1");
    const secondDirectory = path.join(input, "a", "run-2");
    mkdirSync(firstDirectory, { recursive: true });
    mkdirSync(secondDirectory, { recursive: true });

    const firstReport = path.join(firstDirectory, "coverage.cobertura.xml");
    const secondReport = path.join(secondDirectory, "coverage.cobertura.xml");
    writeFileSync(firstReport, cobertura([
      assembly("Zeta.Assembly", [
        '<line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />',
        '<line number="11" hits="0" branch="False" />',
        '<line number="12" hits="2" branch="True" condition-coverage="50% (1/2)" />',
      ]),
      assembly("Alpha.Assembly", [
        '<line number="20" hits="1" branch="True" condition-coverage="50% (1/2)" />',
        '<line number="21" hits="0" branch="False" />',
      ]),
    ]));
    writeFileSync(secondReport, cobertura([
      assembly("Alpha.Assembly", [
        '<line number="30" hits="0" branch="True" condition-coverage="66.67% (2/3)" />',
        '<line number="31" hits="1" branch="False" />',
      ]),
    ]));

    const ignoredMalformed = path.join(input, "coverage.cobertura.xml.bak");
    writeFileSync(ignoredMalformed, "not XML");
    const rawReportsBefore = new Map([
      [firstReport, readFileSync(firstReport)],
      [secondReport, readFileSync(secondReport)],
      [ignoredMalformed, readFileSync(ignoredMalformed)],
    ]);

    const firstJson = path.join(root, "out-1", "summary.json");
    const firstMarkdown = path.join(root, "out-1", "summary.md");
    const secondJson = path.join(root, "out-2", "summary.json");
    const secondMarkdown = path.join(root, "out-2", "summary.md");

    const firstRun = run(input, firstJson, firstMarkdown);
    const secondRun = run(input, secondJson, secondMarkdown);

    assert.equal(firstRun.status, 0, firstRun.stderr);
    assert.equal(secondRun.status, 0, secondRun.stderr);
    assert.equal(firstRun.stderr, "");
    assert.equal(secondRun.stderr, "");

    const expectedJson = `${JSON.stringify({
      schema: "cmsify.coverage.v1",
      sourceSha,
      assemblies: [
        {
          assembly: "Alpha.Assembly",
          lines: { valid: 4, covered: 2, percentage: 50 },
          branches: { valid: 5, covered: 3, percentage: 60 },
        },
        {
          assembly: "Zeta.Assembly",
          lines: { valid: 3, covered: 2, percentage: 66.67 },
          branches: { valid: 4, covered: 2, percentage: 50 },
        },
      ],
    }, null, 2)}\n`;
    const expectedMarkdown = `# Coverage trend\n\nSource SHA: \`${sourceSha}\`\n\n| Assembly | Lines | Line coverage | Branches | Branch coverage |\n| --- | ---: | ---: | ---: | ---: |\n| Alpha.Assembly | 2 / 4 | 50.00% | 3 / 5 | 60.00% |\n| Zeta.Assembly | 2 / 3 | 66.67% | 2 / 4 | 50.00% |\n`;

    assert.equal(readFileSync(firstJson, "utf8"), expectedJson);
    assert.equal(readFileSync(firstMarkdown, "utf8"), expectedMarkdown);
    assert.deepEqual(readFileSync(secondJson), readFileSync(firstJson));
    assert.deepEqual(readFileSync(secondMarkdown), readFileSync(firstMarkdown));
    for (const [report, bytes] of rawReportsBefore) {
      assert.deepEqual(readFileSync(report), bytes, report);
    }
  });
});

test("records zero coverage as trend data without threshold or pass/fail semantics", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(path.join(input, "coverage.cobertura.xml"), cobertura([
      assembly("Uncovered.Assembly", [
        '<line number="1" hits="0" branch="False" />',
      ]),
    ]));
    const json = path.join(root, "summary.json");
    const markdown = path.join(root, "summary.md");

    const result = run(input, json, markdown);

    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(readFileSync(json, "utf8")).assemblies, [{
      assembly: "Uncovered.Assembly",
      lines: { valid: 1, covered: 0, percentage: 0 },
      branches: { valid: 0, covered: 0, percentage: 100 },
    }]);
    assert.doesNotMatch(readFileSync(json, "utf8"), /threshold|passed|failed|status/i);
    assert.doesNotMatch(readFileSync(markdown, "utf8"), /threshold|passed|failed|status/i);
  });
});

test("binds summaries to the explicit GitHub source SHA", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(path.join(input, "coverage.cobertura.xml"), cobertura([
      assembly("Assembly", ['<line number="1" hits="1" branch="False" />']),
    ]));
    const json = path.join(root, "summary.json");

    const result = run(input, json, path.join(root, "summary.md"), {
      GITHUB_SHA: "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
    });

    assert.equal(result.status, 0, result.stderr);
    assert.equal(
      JSON.parse(readFileSync(json, "utf8")).sourceSha,
      "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
    );
  });
});

test("fails without an exact Cobertura report", () => {
  withTemporaryDirectory((root) => {
    const emptyInput = path.join(root, "empty");
    mkdirSync(emptyInput, { recursive: true });
    writeFileSync(path.join(emptyInput, "nested.coverage.cobertura.xml"), "not XML");

    for (const input of [path.join(root, "missing"), emptyInput]) {
      const result = run(input, path.join(root, "summary.json"), path.join(root, "summary.md"));

      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /coverage\.cobertura\.xml/i);
    }
  });
});

test("fails on malformed Cobertura without writing summaries", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(
      path.join(input, "coverage.cobertura.xml"),
      '<coverage><packages><package name="Broken"><classes>',
    );
    const json = path.join(root, "summary.json");
    const markdown = path.join(root, "summary.md");

    const result = run(input, json, markdown);

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /malformed.*Cobertura/i);
    assert.equal(result.stdout, "");
    assert.throws(() => readFileSync(json));
    assert.throws(() => readFileSync(markdown));
  });
});
