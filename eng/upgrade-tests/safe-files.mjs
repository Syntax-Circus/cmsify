import { randomBytes } from "node:crypto";
import { constants } from "node:fs";
import { lstat, mkdir, open, rename, rm } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";

export const MAX_SAFE_JSON_BYTES = 64 * 1024;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function containedBy(root, candidate) {
  const pathFromRoot = relative(root, candidate);
  return pathFromRoot === "" || (!pathFromRoot.startsWith(`..${sep}`) && pathFromRoot !== ".." && !isAbsolute(pathFromRoot));
}

async function statWithoutLinks(path, description) {
  const stat = await lstat(path);
  assert(!stat.isSymbolicLink(), `${description} contains a linked or reparse path.`);
  assert(!stat.isFile() || stat.nlink === 1, `${description} contains a linked file.`);
  return stat;
}

/** Rejects links/junctions in every existing component below a trusted root. */
export async function assertPhysicalPath(root, target, { leaf = "any", allowMissing = false } = {}) {
  const trustedRoot = resolve(root);
  const resolvedTarget = resolve(target);
  assert(containedBy(trustedRoot, resolvedTarget), "Safe path escapes its trusted root.");
  await statWithoutLinks(trustedRoot, "Safe path");
  const pathFromRoot = relative(trustedRoot, resolvedTarget);
  let current = trustedRoot;
  const parts = pathFromRoot === "" ? [] : pathFromRoot.split(sep);
  for (let index = 0; index < parts.length; index += 1) {
    current = resolve(current, parts[index]);
    let stat;
    try {
      stat = await statWithoutLinks(current, "Safe path");
    } catch (error) {
      if (error?.code === "ENOENT" && allowMissing) return;
      throw error;
    }
    const isLeaf = index === parts.length - 1;
    if (!isLeaf) assert(stat.isDirectory(), "Safe path parent must be a real directory.");
    else if (leaf === "file") assert(stat.isFile(), "Safe path leaf must be a regular file.");
    else if (leaf === "directory") assert(stat.isDirectory(), "Safe path leaf must be a real directory.");
  }
}

/** Creates missing directories one component at a time and rejects linked parents. */
export async function ensureSafeDirectory(root, directory) {
  const trustedRoot = resolve(root);
  const resolvedDirectory = resolve(directory);
  assert(containedBy(trustedRoot, resolvedDirectory), "Safe directory escapes its trusted root.");
  await statWithoutLinks(trustedRoot, "Safe directory");
  let current = trustedRoot;
  const pathFromRoot = relative(trustedRoot, resolvedDirectory);
  for (const part of pathFromRoot === "" ? [] : pathFromRoot.split(sep)) {
    current = resolve(current, part);
    try {
      const stat = await statWithoutLinks(current, "Safe directory");
      assert(stat.isDirectory(), "Safe directory component must be a real directory.");
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
      await mkdir(current);
      const stat = await statWithoutLinks(current, "Safe directory");
      assert(stat.isDirectory(), "Safe directory component must be a real directory.");
    }
  }
}

/** Opens a regular file without following its leaf and verifies the opened identity. */
export async function openSafeRegularFile(root, path) {
  await assertPhysicalPath(root, path, { leaf: "file" });
  const before = await lstat(path);
  const flags = constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0);
  const handle = await open(path, flags);
  try {
    const opened = await handle.stat();
    assert(opened.isFile() && !opened.isSymbolicLink(), "Safe file must be regular and unlinked.");
    if (before.ino !== 0 && opened.ino !== 0) assert(before.dev === opened.dev && before.ino === opened.ino, "Safe file identity changed while opening.");
    return handle;
  } catch (error) {
    await handle.close();
    throw error;
  }
}

export async function readSafeFile(root, path, encoding) {
  const handle = await openSafeRegularFile(root, path);
  try {
    return await handle.readFile(encoding);
  } finally {
    await handle.close();
  }
}

/** Writes through an exclusive, no-follow temporary leaf and atomically renames it. */
export async function writeSafeAtomically(root, path, contents, options = {}) {
  const parent = resolve(path, "..");
  await ensureSafeDirectory(root, parent);
  await assertPhysicalPath(root, path, { leaf: "file", allowMissing: true });
  const temporary = resolve(parent, `.${randomBytes(12).toString("hex")}.tmp`);
  const flags = constants.O_CREAT | constants.O_EXCL | constants.O_WRONLY | (constants.O_NOFOLLOW ?? 0);
  let handle;
  try {
    handle = await open(temporary, flags, options.mode ?? 0o600);
    await handle.writeFile(contents, options.encoding ? { encoding: options.encoding } : undefined);
    await handle.sync();
    await handle.close();
    handle = undefined;
    await assertPhysicalPath(root, temporary, { leaf: "file" });
    await assertPhysicalPath(root, path, { leaf: "file", allowMissing: true });
    await rename(temporary, path);
    await assertPhysicalPath(root, path, { leaf: "file" });
  } finally {
    if (handle) await handle.close().catch(() => undefined);
    await rm(temporary, { force: true }).catch(() => undefined);
  }
}

/** Serializes bounded JSON through the same safe atomic file path as other run artifacts. */
export async function writeBoundedJsonAtomically(root, path, value, options = {}) {
  const { maxBytes = MAX_SAFE_JSON_BYTES, ...writeOptions } = options;
  assert(Number.isSafeInteger(maxBytes) && maxBytes > 0 && maxBytes <= MAX_SAFE_JSON_BYTES, "Safe JSON size limit is invalid.");
  const contents = `${JSON.stringify(value, null, 2)}\n`;
  assert(Buffer.byteLength(contents, "utf8") <= maxBytes, "Safe JSON artifact exceeds its size limit.");
  await writeSafeAtomically(root, path, contents, writeOptions);
}
