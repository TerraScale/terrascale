import { readdir, readFile, realpath, stat } from "node:fs/promises";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("./", import.meta.url));
const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};
const inside = (base, path) => {
  const part = relative(base, path);
  return part !== ".." && !part.startsWith(`..${sep}`) && !isAbsolute(part);
};

function scalar(value) {
  if (value.startsWith('"')) return JSON.parse(value);
  if (value.startsWith("'")) {
    assert(/^'(?:[^']|'')*'$/.test(value), "invalid single-quoted YAML string");
    return value.slice(1, -1).replaceAll("''", "'");
  }
  assert(value && !/^[\[\]{}!&*|>@`%#?:,\-]/.test(value) &&
    !/:\s|:$|\s#|[\x00-\x1f]/.test(value), "use a quoted YAML string for this scalar");
  assert(!/^(?:null|~|true|false|[-+]?(?:0o[0-7]+|0x[0-9a-f]+|(?:\d+(?:\.\d*)?|\.\d+)(?:e[-+]?\d+)?|\.inf)|\.nan)$/i.test(value), "frontmatter values must be strings");
  return value;
}

function frontmatter(text) {
  const match = text.match(/^---\n([\s\S]*?)\n---(?:\n|$)/);
  assert(match, "missing YAML frontmatter");
  const lines = match[1].split("\n");
  const fields = {};
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].trim()) continue;
    const field = lines[i].match(/^(name|description):[ ]+(.*)$/);
    assert(field, "frontmatter must contain only name and description strings");
    const [, key, value] = field;
    assert(!Object.hasOwn(fields, key), `duplicate frontmatter field: ${key}`);
    if (/^[|>][+-]?$/.test(value)) {
      const block = [];
      while (i + 1 < lines.length && /^(?:  |$)/.test(lines[i + 1])) {
        const line = lines[++i];
        block.push(line.slice(2));
      }
      const joined = block.map((line, index) => line +
        (value[0] === ">" && line && block[index + 1] ? " " : "\n")).join("");
      fields[key] = value.endsWith("-") ? joined.trimEnd() :
        value.endsWith("+") ? joined : `${joined.trimEnd()}\n`;
    } else {
      fields[key] = scalar(value.trimEnd());
    }
    assert(typeof fields[key] === "string", `${key} must be a string`);
  }
  return { fields, body: text.slice(match[0].length) };
}

async function filesIn(folder) {
  const files = [];
  for (const entry of await readdir(folder, { withFileTypes: true })) {
    const path = resolve(folder, entry.name);
    assert(!entry.isSymbolicLink(), `${path}: symlinks are not allowed`);
    if (entry.isDirectory()) files.push(...await filesIn(path));
    else if (entry.isFile()) files.push(path);
  }
  return files;
}

function inlineProse(text) {
  let prose = "";
  for (let i = 0; i < text.length;) {
    if (text[i] === "\\" && /[\\`]/.test(text[i + 1] ?? "")) {
      prose += text.slice(i, i + 2);
      i += 2;
      continue;
    }
    if (text[i] !== "`") {
      prose += text[i++];
      continue;
    }
    const opening = text.slice(i).match(/^`+/)[0];
    const runs = /`+/g;
    runs.lastIndex = i + opening.length;
    let closing = runs.exec(text);
    while (closing && closing[0].length !== opening.length) closing = runs.exec(text);
    if (closing) {
      const end = closing.index + closing[0].length;
      prose += text.slice(i, end).replace(/[^\n]/g, " ");
      i = end;
    } else {
      prose += opening;
      i += opening.length;
    }
  }
  return prose;
}

function markdownProse(markdown) {
  let fence;
  const text = markdown.split("\n").map(line => {
    const marker = line.match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (fence) {
      if (marker && marker[1][0] === fence[0] && marker[1].length >= fence.length && !marker[2].trim()) {
        fence = undefined;
      }
      return "";
    }
    if (marker && (marker[1][0] === "~" || !marker[2].includes("`"))) {
      fence = marker[1];
      return "";
    }
    return line;
  }).join("\n");
  return text.split(/(\n[ \t]*\n)/).map(inlineProse).join("");
}

function destinations(markdown) {
  const text = markdownProse(markdown);
  const targets = [];
  for (const match of text.matchAll(/\]\(\s*|^ {0,3}\[[^\]\n]+\]:\s*/gm)) {
    const rest = text.slice(match.index + match[0].length);
    if (rest.startsWith("<")) {
      const end = rest.indexOf(">");
      assert(end >= 0, "unterminated link destination");
      targets.push(rest.slice(1, end));
      continue;
    }
    let depth = 0;
    let target = "";
    for (let i = 0; i < rest.length; i++) {
      const char = rest[i];
      if (char === "\\" && i + 1 < rest.length) {
        target += rest[++i];
        continue;
      }
      if (/\s/.test(char) || (char === ")" && depth === 0)) break;
      if (char === "(") depth++;
      if (char === ")") depth--;
      target += char;
    }
    targets.push(target);
  }
  for (const match of text.matchAll(/\b(?:href|src)\s*=\s*["']([^"']+)["']/gi)) targets.push(match[1]);
  return targets;
}

async function validate(folder) {
  const path = resolve(folder, "SKILL.md");
  const files = await filesIn(folder);
  assert(files.includes(path), "missing SKILL.md");
  const text = (await readFile(path, "utf8")).replaceAll("\r\n", "\n");
  assert(text.replace(/\n$/, "").split("\n").length <= 500, "SKILL.md exceeds 500 lines");
  const { fields, body } = frontmatter(text);
  const name = relative(root, folder);
  assert(fields.name === name, "name must equal the folder name");
  assert(/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(name) && name.length <= 64, "invalid name or name exceeds 64 characters");
  assert(typeof fields.description === "string" && fields.description.trim() &&
    [...fields.description].length <= 1024, "description must contain 1–1024 characters");
  assert(body.trim(), "SKILL.md body is empty");
  for (const file of files.filter(path => path.endsWith(".md"))) {
    const markdown = file === path ? body : await readFile(file, "utf8");
    for (const target of destinations(markdown)) {
      if (/^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(target)) continue;
      const link = decodeURIComponent(target.split(/[?#]/)[0].replace(/%(?![0-9a-f]{2})/gi, "%25"));
      if (!link) continue;
      const resolved = resolve(dirname(file), link);
      assert(inside(folder, resolved), `${file}: link leaves skill folder: ${target}`);
      assert(inside(folder, await realpath(resolved)) && (await stat(resolved)).isFile(),
        `${file}: link must resolve to a file inside the skill: ${target}`);
    }
  }
}

try {
  assert(process.argv.slice(2).every(arg => arg === "--cli-list"), "unknown argument");
  const entries = await readdir(root, { withFileTypes: true });
  assert(entries.every(entry => entry.isDirectory() || (entry.isFile() && (entry.name === "README.md" || entry.name === "validate-skills.mjs"))),
    "sdk/skills/ must contain only skill folders, README.md, and validate-skills.mjs");
  const names = entries.filter(entry => entry.isDirectory()).map(entry => entry.name).sort();
  for (const name of names) {
    try { await validate(resolve(root, name)); }
    catch (error) { throw new Error(`${name}: ${error.message}`); }
  }
  console.log(`Validated ${names.length} skill(s).`);
  if (process.argv.includes("--cli-list")) {
    const result = spawnSync("npx", ["-y", "skills", "add", ".", "--list"], {
      cwd: root, encoding: "utf8", env: { ...process.env, DISABLE_TELEMETRY: "1", NO_COLOR: "1" },
    });
    if (result.error) throw result.error;
    const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;
    process.stdout.write(output);
    const empty = names.length === 0 && result.status === 1 && output.includes("[SKILLS_NO_VALID_SKILLS]");
    assert(result.status === 0 || empty, `skills CLI exited with ${result.status}`);
    const lines = output.replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, "").split("\n").map(line => line.replace(/^[│┃]\s*/, "").trim());
    for (const name of names) assert(lines.includes(name), `CLI did not list skill: ${name}`);
    console.log(empty ? "CLI discovery passed: empty catalog (expected no-skills result)." :
      `CLI discovery passed: listed all ${names.length} skill(s).`);
  }
} catch (error) {
  console.error(`Validation failed: ${error.message}`);
  process.exitCode = 1;
}
