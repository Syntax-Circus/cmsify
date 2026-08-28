import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
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
  const env = { ...process.env, GITHUB_SHA: sourceSha, ...environment };
  for (const [name, value] of Object.entries(env)) {
    if (value === null) delete env[name];
  }
  return spawnSync(process.execPath, [
    summarizer,
    "--input", input,
    "--json", json,
    "--markdown", markdown,
  ], {
    cwd: repositoryRoot,
    encoding: "utf8",
    env,
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

test("rejects colliding JSON and Markdown paths before changing an existing output", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(path.join(input, "coverage.cobertura.xml"), cobertura([
      assembly("Assembly", ['<line number="1" hits="1" branch="False" />']),
    ]));
    const output = path.join(root, "summary.out");
    const sentinel = Buffer.from("existing summary\n");
    writeFileSync(output, sentinel);

    const result = run(input, output, path.join(root, ".", "summary.out"));

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /JSON.*Markdown.*distinct|output paths.*distinct/i);
    assert.deepEqual(readFileSync(output), sentinel);
  });
});

test("rejects output aliases of a raw report without modifying the report", (context) => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    const report = path.join(input, "coverage.cobertura.xml");
    const reportContents = cobertura([
      assembly("Assembly", ['<line number="1" hits="1" branch="False" />']),
    ]);
    const aliases = [
      report,
      path.join(input, "nested", "..", "coverage.cobertura.xml"),
    ];

    const link = path.join(root, "raw-link");
    try {
      symlinkSync(input, link, process.platform === "win32" ? "junction" : "dir");
      aliases.push(path.join(link, "coverage.cobertura.xml"));
    } catch (error) {
      context.diagnostic(`Symlink alias case unavailable on this platform: ${error.message}`);
    }
    if (process.platform === "win32") aliases.push(report.toUpperCase());

    for (const [index, alias] of aliases.entries()) {
      writeFileSync(report, reportContents);
      const before = readFileSync(report);
      const markdown = path.join(root, `summary-${index}.md`);

      const result = run(input, alias, markdown);

      assert.notEqual(result.status, 0, alias);
      assert.match(result.stderr, /output.*raw.*coverage|raw.*report/i, alias);
      assert.deepEqual(readFileSync(report), before, alias);
      assert.equal(existsSync(markdown), false, alias);
    }
  });
});

test("does not replace either summary when an output destination is invalid", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(path.join(input, "coverage.cobertura.xml"), cobertura([
      assembly("Assembly", ['<line number="1" hits="1" branch="False" />']),
    ]));
    const json = path.join(root, "summary.json");
    const sentinel = Buffer.from("existing JSON\n");
    writeFileSync(json, sentinel);
    const invalidMarkdown = path.join(root, "summary-directory");
    mkdirSync(invalidMarkdown);

    const result = run(input, json, invalidMarkdown);

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /output.*file|destination/i);
    assert.deepEqual(readFileSync(json), sentinel);
  });
});

test("counts only real XML elements and ignores markup-shaped lexical content", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(path.join(input, "coverage.cobertura.xml"), `<?xml version="1.0"?>
<?audit <package name="Processing.Assembly"><classes><class><lines><line hits="1" /></lines></class></classes></package>?>
<!DOCTYPE coverage SYSTEM "coverage.dtd">
<coverage>
  <!-- <package name="Comment.Assembly"><classes><class><lines><line hits="1" /></lines></class></classes></package> -->
  <packages>
    <![CDATA[<package name="Cdata.Assembly"><classes><class><lines><line hits="1" /></lines></class></classes></package>]]>
    ${assembly("Real.Assembly", ['<line number="1" hits="1" branch="False" />'])}
  </packages>
</coverage>
`);
    const json = path.join(root, "summary.json");

    const result = run(input, json, path.join(root, "summary.md"));

    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(readFileSync(json, "utf8")).assemblies, [{
      assembly: "Real.Assembly",
      lines: { valid: 1, covered: 1, percentage: 100 },
      branches: { valid: 0, covered: 0, percentage: 100 },
    }]);
  });
});

test("rejects malformed attributes, entities, comments, and document structure", () => {
  const lineTree = '<classes><class name="Example"><lines><line number="1" hits="1" branch="False" /></lines></class></classes>';
  const malformedReports = [
    `<coverage><packages><package name=Broken>${lineTree}</package></packages></coverage>`,
    `<coverage><packages><package name="Broken&unknown;">${lineTree}</package></packages></coverage>`,
    `<coverage><!-- invalid -- comment --><packages><package name="Broken">${lineTree}</package></packages></coverage>`,
    `<coverage><packages><package name="First">${lineTree}</package></packages></coverage><coverage />`,
    `<coverage><packages><package name="Broken">${lineTree}<line number="2" hits="1" /junk></package></packages></coverage>`,
    `<?audit<data?>\n<coverage><packages><package name="Broken">${lineTree}</package></packages></coverage>`,
    `<?xml version="1.0" unexpected="value"?>\n<coverage><packages><package name="Broken">${lineTree}</package></packages></coverage>`,
    `<coverage><packages><package name="Broken"line-rate="1">${lineTree}</package></packages></coverage>`,
    `<?xml version="1.0"encoding="utf-8"?>\n<coverage><packages><package name="Broken">${lineTree}</package></packages></coverage>`,
    `<coverage><!-- invalid ---><packages><package name="Broken">${lineTree}</package></packages></coverage>`,
  ];

  for (const [index, xml] of malformedReports.entries()) {
    withTemporaryDirectory((root) => {
      const input = path.join(root, "raw");
      mkdirSync(input, { recursive: true });
      writeFileSync(path.join(input, "coverage.cobertura.xml"), xml);
      const json = path.join(root, `summary-${index}.json`);
      const markdown = path.join(root, `summary-${index}.md`);

      const result = run(input, json, markdown);

      assert.notEqual(result.status, 0, `fixture ${index}`);
      assert.match(result.stderr, /malformed.*Cobertura/i, `fixture ${index}`);
      assert.equal(existsSync(json), false, `fixture ${index}`);
      assert.equal(existsSync(markdown), false, `fixture ${index}`);
    });
  }
});

test("counts class lines once when method lines duplicate them", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    const duplicatedLines = [
      '<line number="1" hits="1" branch="True" condition-coverage="50% (1/2)" />',
      '<line number="2" hits="0" branch="False" />',
    ].join("\n");
    writeFileSync(path.join(input, "coverage.cobertura.xml"), `<coverage><packages>
      <package name="Method.Assembly"><classes><class name="Example">
        <methods><method name="Run"><lines>${duplicatedLines}</lines></method></methods>
        <lines>${duplicatedLines}</lines>
      </class></classes></package>
    </packages></coverage>`);
    const json = path.join(root, "summary.json");

    const result = run(input, json, path.join(root, "summary.md"));

    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(readFileSync(json, "utf8")).assemblies, [{
      assembly: "Method.Assembly",
      lines: { valid: 2, covered: 1, percentage: 50 },
      branches: { valid: 2, covered: 1, percentage: 50 },
    }]);
  });
});

test("uses local Git HEAD when GITHUB_SHA is absent and rejects an invalid explicit SHA", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(path.join(input, "coverage.cobertura.xml"), cobertura([
      assembly("Assembly", ['<line number="1" hits="1" branch="False" />']),
    ]));
    const json = path.join(root, "summary.json");
    const localHead = execFileSync("git", ["rev-parse", "HEAD"], {
      cwd: repositoryRoot,
      encoding: "utf8",
    }).trim();

    const localResult = run(input, json, path.join(root, "summary.md"), { GITHUB_SHA: null });

    assert.equal(localResult.status, 0, localResult.stderr);
    assert.equal(JSON.parse(readFileSync(json, "utf8")).sourceSha, localHead);

    const invalidJson = path.join(root, "invalid.json");
    const invalidResult = run(input, invalidJson, path.join(root, "invalid.md"), {
      GITHUB_SHA: "not-a-commit",
    });

    assert.notEqual(invalidResult.status, 0);
    assert.match(invalidResult.stderr, /40-character source SHA/i);
    assert.equal(existsSync(invalidJson), false);
  });
});

test("escapes Markdown table delimiters and backslashes in assembly names", () => {
  withTemporaryDirectory((root) => {
    const input = path.join(root, "raw");
    mkdirSync(input, { recursive: true });
    writeFileSync(path.join(input, "coverage.cobertura.xml"), cobertura([
      assembly("Pipe|Back\\Slash", ['<line number="1" hits="1" branch="False" />']),
    ]));
    const markdown = path.join(root, "summary.md");

    const result = run(input, path.join(root, "summary.json"), markdown);

    assert.equal(result.status, 0, result.stderr);
    assert.match(readFileSync(markdown, "utf8"), /\| Pipe\\\|Back\\\\Slash \|/);
  });
});
