function fail(sourceName, lineNumber, message) {
  const location = lineNumber === null ? sourceName : `${sourceName}:${lineNumber}`;
  throw new Error(`${location}: ${message}`);
}

function stripComment(value, sourceName, lineNumber) {
  let quote = null;
  let escaped = false;
  for (let index = 0; index < value.length; index += 1) {
    const character = value[index];
    if (quote === '"') {
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === quote) quote = null;
      continue;
    }
    if (quote === "'") {
      if (character === "'" && value[index + 1] === "'") index += 1;
      else if (character === quote) quote = null;
      continue;
    }
    if (character === '"' || character === "'") {
      quote = character;
    } else if (character === "#" && (index === 0 || /\s/.test(value[index - 1]))) {
      return value.slice(0, index).trimEnd();
    }
  }
  if (quote !== null) fail(sourceName, lineNumber, "unterminated quoted scalar");
  return value.trimEnd();
}

function stripPlainComment(value) {
  for (let index = 0; index < value.length; index += 1) {
    if (value[index] === "#" && (index === 0 || /\s/.test(value[index - 1]))) {
      return value.slice(0, index).trimEnd();
    }
  }
  return value.trimEnd();
}

function stripScalarComment(value, sourceName, lineNumber) {
  const token = value.trim();
  return /^["'\[{]/.test(token)
    ? stripComment(token, sourceName, lineNumber)
    : stripPlainComment(token);
}

function splitFlow(value, sourceName, lineNumber) {
  const parts = [];
  let start = 0;
  let quote = null;
  let escaped = false;
  let squareDepth = 0;
  let curlyDepth = 0;
  for (let index = 0; index < value.length; index += 1) {
    const character = value[index];
    if (quote === '"') {
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === quote) quote = null;
      continue;
    }
    if (quote === "'") {
      if (character === "'" && value[index + 1] === "'") index += 1;
      else if (character === quote) quote = null;
      continue;
    }
    if (character === '"' || character === "'") quote = character;
    else if (character === "[") squareDepth += 1;
    else if (character === "]") squareDepth -= 1;
    else if (character === "{") curlyDepth += 1;
    else if (character === "}") curlyDepth -= 1;
    else if (character === "," && squareDepth === 0 && curlyDepth === 0) {
      parts.push(value.slice(start, index).trim());
      start = index + 1;
    }
    if (squareDepth < 0 || curlyDepth < 0) fail(sourceName, lineNumber, "unbalanced flow value");
  }
  if (quote !== null || squareDepth !== 0 || curlyDepth !== 0) {
    fail(sourceName, lineNumber, "unbalanced flow value");
  }
  parts.push(value.slice(start).trim());
  if (parts.some((part) => part.length === 0)) fail(sourceName, lineNumber, "empty flow item");
  return parts;
}

function flowPair(value, sourceName, lineNumber) {
  let quote = null;
  let escaped = false;
  let squareDepth = 0;
  let curlyDepth = 0;
  for (let index = 0; index < value.length; index += 1) {
    const character = value[index];
    if (quote === '"') {
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === quote) quote = null;
      continue;
    }
    if (quote === "'") {
      if (character === "'" && value[index + 1] === "'") index += 1;
      else if (character === quote) quote = null;
      continue;
    }
    if (character === '"' || character === "'") quote = character;
    else if (character === "[") squareDepth += 1;
    else if (character === "]") squareDepth -= 1;
    else if (character === "{") curlyDepth += 1;
    else if (character === "}") curlyDepth -= 1;
    else if (character === ":" && squareDepth === 0 && curlyDepth === 0) {
      return [value.slice(0, index).trim(), value.slice(index + 1).trim()];
    }
  }
  fail(sourceName, lineNumber, "flow mapping entry requires a colon");
}

function parseKey(value, sourceName, lineNumber) {
  if (!/^[A-Za-z0-9_.-]+$/.test(value) || ["__proto__", "constructor", "prototype"].includes(value)) {
    fail(sourceName, lineNumber, `unsupported mapping key ${JSON.stringify(value)}`);
  }
  return value;
}

function setUnique(target, key, value, sourceName, lineNumber) {
  if (Object.hasOwn(target, key)) fail(sourceName, lineNumber, `duplicate mapping key ${key}`);
  target[key] = value;
}

function parseScalar(value, sourceName, lineNumber) {
  const token = value.trim();
  const scalar = stripScalarComment(token, sourceName, lineNumber).trim();
  if (scalar === "") fail(sourceName, lineNumber, "empty scalar");
  if (scalar.startsWith('"')) {
    try {
      const parsed = JSON.parse(scalar);
      if (typeof parsed !== "string") throw new Error("not a string");
      return parsed;
    } catch {
      fail(sourceName, lineNumber, "invalid double-quoted scalar");
    }
  }
  if (scalar.startsWith("'")) {
    if (!/^'(?:[^']|'')*'$/.test(scalar)) fail(sourceName, lineNumber, "invalid single-quoted scalar");
    return scalar.slice(1, -1).replaceAll("''", "'");
  }
  if (scalar.startsWith("[")) {
    if (!scalar.endsWith("]")) fail(sourceName, lineNumber, "unterminated flow sequence");
    const body = scalar.slice(1, -1).trim();
    return body === "" ? [] : splitFlow(body, sourceName, lineNumber)
      .map((item) => parseScalar(item, sourceName, lineNumber));
  }
  if (scalar.startsWith("{")) {
    if (!scalar.endsWith("}")) fail(sourceName, lineNumber, "unterminated flow mapping");
    const result = {};
    const body = scalar.slice(1, -1).trim();
    if (body === "") return result;
    for (const item of splitFlow(body, sourceName, lineNumber)) {
      const [rawKey, rawValue] = flowPair(item, sourceName, lineNumber);
      const key = parseKey(rawKey, sourceName, lineNumber);
      setUnique(result, key, parseScalar(rawValue, sourceName, lineNumber), sourceName, lineNumber);
    }
    return result;
  }
  if (/^(?:true|false)$/.test(scalar)) return scalar === "true";
  if (/^(?:null|~)$/.test(scalar)) return null;
  if (/^-?(?:0|[1-9]\d*)(?:\.\d+)?$/.test(scalar)) return Number(scalar);
  if (/^(?:[&*!]|<<\s*:|---$|\.\.\.$)/.test(scalar)) {
    fail(sourceName, lineNumber, `unsupported YAML construct ${JSON.stringify(scalar)}`);
  }
  if (/:(?:\s|$)/.test(scalar)) fail(sourceName, lineNumber, "invalid plain scalar colon");
  return scalar;
}

function mappingPair(content, sourceName, lineNumber) {
  const match = /^([A-Za-z0-9_.-]+):(.*)$/.exec(content);
  if (!match) fail(sourceName, lineNumber, "expected a mapping entry");
  return [parseKey(match[1], sourceName, lineNumber), match[2].trimStart()];
}

export function parseYamlSubset(source, sourceName = "<yaml>") {
  if (source.includes("\t")) fail(sourceName, null, "tabs are not supported");
  const rawLines = source.replaceAll("\r\n", "\n").split("\n");
  const lines = rawLines.map((raw, index) => {
    const indent = raw.length - raw.trimStart().length;
    if (indent % 2 !== 0) fail(sourceName, index + 1, "indentation must use two-space levels");
    const content = raw.slice(indent).trimEnd();
    return { raw, indent, content, number: index + 1 };
  });

  const isBlank = (line) => line.content.trim() === "" || line.content.trimStart().startsWith("#");
  const nextMeaningful = (start) => {
    let index = start;
    while (index < lines.length && isBlank(lines[index])) index += 1;
    return index;
  };

  const parseBlockScalar = (start, parentIndent, style) => {
    if (!["|", "|-"].includes(style)) fail(sourceName, lines[start - 1].number, `unsupported block style ${style}`);
    let index = start;
    const content = [];
    while (index < lines.length) {
      const line = lines[index];
      const physicallyBlank = line.raw.trim() === "";
      if (!physicallyBlank && line.indent <= parentIndent) break;
      if (physicallyBlank) content.push("");
      else {
        if (line.indent < parentIndent + 2) fail(sourceName, line.number, "invalid block scalar indentation");
        content.push(line.raw.slice(parentIndent + 2));
      }
      index += 1;
    }
    while (content.at(-1) === "") content.pop();
    const suffix = style === "|" ? "\n" : "";
    return { value: `${content.join("\n")}${suffix}`, next: index };
  };

  const parseValue = (rawValue, index, parentIndent) => {
    const value = stripScalarComment(rawValue, sourceName, lines[index].number).trim();
    if (value === "|" || value === "|-") return parseBlockScalar(index + 1, parentIndent, value);
    if (value !== "") return { value: parseScalar(value, sourceName, lines[index].number), next: index + 1 };
    const childIndex = nextMeaningful(index + 1);
    if (childIndex >= lines.length || lines[childIndex].indent <= parentIndent) {
      return { value: null, next: index + 1 };
    }
    if (lines[childIndex].indent !== parentIndent + 2) {
      fail(sourceName, lines[childIndex].number, "nested value must increase indentation by two spaces");
    }
    return parseNode(childIndex, parentIndent + 2);
  };

  const parseMappingContinuation = (start, indent, target) => {
    let index = start;
    while (true) {
      index = nextMeaningful(index);
      if (index >= lines.length || lines[index].indent < indent) break;
      const line = lines[index];
      if (line.indent > indent) fail(sourceName, line.number, "unexpected mapping indentation");
      if (line.content.startsWith("- ") || line.content === "-") break;
      const [key, rawValue] = mappingPair(line.content, sourceName, line.number);
      const parsed = parseValue(rawValue, index, indent);
      setUnique(target, key, parsed.value, sourceName, line.number);
      index = parsed.next;
    }
    return { value: target, next: index };
  };

  const parseMapping = (start, indent) => parseMappingContinuation(start, indent, {});

  const parseSequence = (start, indent) => {
    const result = [];
    let index = start;
    while (true) {
      index = nextMeaningful(index);
      if (index >= lines.length || lines[index].indent < indent) break;
      const line = lines[index];
      if (line.indent > indent) fail(sourceName, line.number, "unexpected sequence indentation");
      if (!(line.content === "-" || line.content.startsWith("- "))) break;
      const item = line.content.slice(1).trimStart();
      if (item === "") {
        const childIndex = nextMeaningful(index + 1);
        if (childIndex >= lines.length || lines[childIndex].indent !== indent + 2) {
          fail(sourceName, line.number, "empty sequence item requires a nested value");
        }
        const parsed = parseNode(childIndex, indent + 2);
        result.push(parsed.value);
        index = parsed.next;
        continue;
      }
      if (/^[A-Za-z0-9_.-]+:/.test(item)) {
        const object = {};
        const [key, rawValue] = mappingPair(item, sourceName, line.number);
        const parsed = parseValue(rawValue, index, indent + 2);
        setUnique(object, key, parsed.value, sourceName, line.number);
        const continuation = parseMappingContinuation(parsed.next, indent + 2, object);
        result.push(continuation.value);
        index = continuation.next;
      } else {
        result.push(parseScalar(item, sourceName, line.number));
        index += 1;
        const childIndex = nextMeaningful(index);
        if (childIndex < lines.length && lines[childIndex].indent > indent) {
          fail(sourceName, lines[childIndex].number, "scalar sequence item cannot have nested content");
        }
      }
    }
    return { value: result, next: index };
  };

  function parseNode(start, indent) {
    const index = nextMeaningful(start);
    if (index >= lines.length) fail(sourceName, null, "expected a YAML value");
    if (lines[index].indent !== indent) fail(sourceName, lines[index].number, "unexpected indentation");
    return lines[index].content === "-" || lines[index].content.startsWith("- ")
      ? parseSequence(index, indent)
      : parseMapping(index, indent);
  }

  const first = nextMeaningful(0);
  if (first >= lines.length) fail(sourceName, null, "document is empty");
  if (lines[first].indent !== 0) fail(sourceName, lines[first].number, "document must start at indentation zero");
  const parsed = parseNode(first, 0);
  const trailing = nextMeaningful(parsed.next);
  if (trailing < lines.length) fail(sourceName, lines[trailing].number, "multiple documents or trailing content are not supported");
  return parsed.value;
}
