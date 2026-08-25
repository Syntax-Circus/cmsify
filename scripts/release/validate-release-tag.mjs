import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const [tag, ...arguments_] = process.argv.slice(2);
const sourceSha = arguments_[arguments_.indexOf("--source-sha") + 1];
const rootIndex = arguments_.indexOf("--root");
const repositoryRoot = rootIndex === -1
  ? resolve(fileURLToPath(new URL("../..", import.meta.url)))
  : resolve(arguments_[rootIndex + 1] ?? "");
const requireChangelog = arguments_.includes("--require-changelog");

if (!/^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$/.test(tag ?? "")) {
  throw new Error(`Release tag must be a SemVer vX.Y.Z tag (prereleases allowed): ${tag ?? "<missing>"}`);
}
if (!/^[0-9a-f]{40}$/i.test(sourceSha ?? "")) throw new Error("Release source SHA must be a full immutable Git commit SHA.");

const [major, minor, patch] = tag.slice(1).split(/[.-]/, 3).map(Number);
const exceedsPublishedUpgradeBaseline = major === 0 && (minor > 1 || (minor === 1 && patch > 3));

if (exceedsPublishedUpgradeBaseline && !existsSync(resolve(repositoryRoot, "tests", "upgrade", "fixtures", `${tag.slice(1)}.json`))) {
  throw new Error(`Refusing ${tag}: a matching checked-in upgrade fixture manifest is required before any later 0.x promotion.`);
}

if (requireChangelog) {
  const changelogPath = resolve(repositoryRoot, "CHANGELOG.md");
  const version = tag.slice(1).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const datedEntry = new RegExp(`^##\\s+\\[${version}\\]\\s+-\\s+\\d{4}-\\d{2}-\\d{2}\\s*$`, "m");
  const changelog = existsSync(changelogPath) ? readFileSync(changelogPath, "utf8") : "";
  if (!datedEntry.test(changelog)) {
    throw new Error(`Refusing ${tag}: promotion requires an exact dated changelog entry; Unreleased placeholders cannot authorize a release.`);
  }
}

process.stdout.write(`${tag.slice(1)}\n`);
