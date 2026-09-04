#!/usr/bin/env node
// The pinned generator for the public `terrascale.sync.v2.msgpack` protocol
// (GOAL.md ADR 0025): reads the one checked-in language-neutral manifest and
// emits the TypeScript codecs, exact-shape validators, and public types.
// Generated files are committed but are never authority — the manifest is.
//
//   node scripts/generate-msgpack-protocol.ts            # regenerate in place
//   node scripts/generate-msgpack-protocol.ts --check    # fail on drift
//   node scripts/generate-msgpack-protocol.ts --out DIR  # emit into DIR
//
// The output is fully deterministic (no timestamps, no environment): re-running
// the generator over an unchanged manifest and generator must be byte-identical
// to the committed file. The drift test enforces exactly that.
//
// No dependencies: plain Node, the manifest JSON, and node:crypto for the
// provenance digests embedded in the generated header.

import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = path.resolve(path.dirname(scriptPath), "..");
const defaultManifestPath = path.join(
  repositoryRoot,
  "src",
  "msgpack",
  "manifest",
  "terrascale.sync.v2.msgpack.json",
);
const defaultOutputDirectory = path.join(repositoryRoot, "src", "msgpack", "generated");
const outputFileName = "terrascale-sync-v2-msgpack.ts";

const args = process.argv.slice(2);
const checkMode = args.includes("--check");
const outIndex = args.indexOf("--out");
const manifestIndex = args.indexOf("--manifest");
const outDirectory = outIndex >= 0 ? path.resolve(args[outIndex + 1]) : defaultOutputDirectory;
const manifestPath =
  manifestIndex >= 0 ? path.resolve(args[manifestIndex + 1]) : defaultManifestPath;

// --- Manifest validation -----------------------------------------------------

const reservedWords = new Set([
  "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
  "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "import", "in",
  "instanceof", "new", "null", "return", "super", "switch", "this", "throw", "true", "try",
  "typeof", "var", "void", "while", "with", "yield", "let", "static", "await", "of",
]);

function invalid(reason) {
  throw new Error(`manifest ${path.relative(repositoryRoot, manifestPath)}: ${reason}`);
}

const reservedTypeNames = new Set(["message_body", "message", "envelope"]);

const namePattern = /^[a-z][a-z0-9_]*$/u;

function checkName(name, what, allowReservedWords) {
  if (typeof name !== "string" || !namePattern.test(name)) {
    invalid(`${what} name ${JSON.stringify(name)} must match ${namePattern}`);
  }
  if (!allowReservedWords && reservedWords.has(name)) {
    invalid(`${what} name ${JSON.stringify(name)} is a reserved word`);
  }
}

function checkDoc(entry, what) {
  if (entry.doc !== undefined && typeof entry.doc !== "string") {
    invalid(`${what} carries a non-string doc`);
  }
}

function checkFields(fields, what, allowEmpty) {
  if (!Array.isArray(fields) || (!allowEmpty && fields.length === 0)) {
    invalid(`${what} must declare a non-empty field list`);
  }
  const seen = new Set();
  for (const field of fields) {
    if (typeof field !== "object" || field === null) {
      invalid(`${what} has a non-object field`);
    }
    checkName(field.name, `${what} field`);
    if (seen.has(field.name)) {
      invalid(`${what} repeats the field name ${field.name}`);
    }
    seen.add(field.name);
    checkDoc(field, `${what}.${field.name}`);
    checkType(field.type, `${what}.${field.name}`);
  }
}

function checkType(type, where) {
  if (typeof type !== "object" || type === null || typeof type.kind !== "string") {
    invalid(`${where} has no kind`);
  }
  switch (type.kind) {
    case "u32":
    case "u64":
    case "i64":
    case "f64":
    case "bool":
      return;
    case "text": {
      const minBytes = type.minBytes ?? 0;
      if (
        !Number.isInteger(minBytes) || minBytes < 0
        || !Number.isInteger(type.maxBytes) || type.maxBytes < minBytes
        || type.maxBytes > manifest.scan.maxStringBytes
      ) {
        invalid(`${where}: text bounds ${minBytes}..${type.maxBytes} must fit the scan string bound`);
      }
      return;
    }
    case "binary": {
      if (
        !Number.isInteger(type.minBytes) || !Number.isInteger(type.maxBytes)
        || type.minBytes < 0 || type.minBytes > type.maxBytes
        || type.maxBytes > manifest.scan.maxBinaryBytes
      ) {
        invalid(`${where}: binary bounds ${type.minBytes}..${type.maxBytes} are not within the scan binary bound`);
      }
      return;
    }
    case "array": {
      if (!Number.isInteger(type.maxItems) || type.maxItems < 0 || type.maxItems > manifest.scan.maxArrayItems) {
        invalid(`${where}: array maxItems ${type.maxItems} must fit the scan array bound`);
      }
      if (type.aggregateBytes !== undefined) {
        if (typeof type.aggregateBytes !== "object" || type.aggregateBytes === null
          || typeof type.aggregateBytes.field !== "string"
          || !Number.isInteger(type.aggregateBytes.maxBytes)
          || type.aggregateBytes.maxBytes < 0
          || type.aggregateBytes.maxBytes > manifest.scan.maxBinaryBytes) {
          invalid(`${where}: aggregateBytes must name a field and bounded maxBytes`);
        }
      }
      checkType(type.items, `${where} items`);
      return;
    }
    case "option": {
      checkType(type.of, `${where} option`);
      return;
    }
    case "tuple": {
      if (!Array.isArray(type.of) || type.of.length === 0) {
        invalid(`${where}: tuple must declare at least one element`);
      }
      type.of.forEach((element, index) => checkType(element, `${where}[${index}]`));
      return;
    }
    case "ref": {
      if (!declaredTypeNames.has(type.name)) {
        invalid(`${where} references the undeclared type ${type.name}`);
      }
      return;
    }
    default:
      invalid(`${where} has the unknown kind ${type.kind}`);
  }
}

let manifest;

function validateManifest(raw) {
  manifest = raw;
  if (manifest.manifestVersion !== 1) {
    invalid("manifestVersion must be 1");
  }
  if (manifest.protocol !== "terrascale.sync.v2.msgpack") {
    invalid(`protocol ${manifest.protocol} is not generated by this pinned generator`);
  }
  if (!Number.isInteger(manifest.protocolMajor) || manifest.protocolMajor < 0) {
    invalid("protocolMajor must be a non-negative integer");
  }
  if (!Number.isInteger(manifest.protocolMinor) || manifest.protocolMinor < 0) {
    invalid("protocolMinor must be a non-negative integer");
  }
  if (!Number.isInteger(manifest.supportedFeatures) || manifest.supportedFeatures < 0 || manifest.supportedFeatures > 0xffff_ffff) {
    invalid("supportedFeatures must be a u32");
  }
  checkDoc(manifest, "manifest");

  const scan = manifest.scan;
  if (!scan
    || !Number.isInteger(scan.maxMessageBytes) || scan.maxMessageBytes <= 0
    || !Number.isInteger(scan.maxDepth) || scan.maxDepth <= 0
    || !Number.isInteger(scan.maxStringBytes) || scan.maxStringBytes <= 0
    || !Number.isInteger(scan.maxBinaryBytes) || scan.maxBinaryBytes <= 0
    || !Number.isInteger(scan.maxArrayItems) || scan.maxArrayItems <= 0
    || !Number.isInteger(scan.maxMapEntries) || scan.maxMapEntries < 0) {
    invalid("the scan limits are missing or malformed");
  }
  // The TypeScript core has no map or extension accessors at all: the indexed
  // array contract is structural, so the manifest must forbid both.
  if (scan.maxMapEntries !== 0) {
    invalid("the scan limits must forbid maps (maxMapEntries 0): the public protocol is indexed arrays only");
  }
  if (scan.extensionsAllowed !== false) {
    invalid("the scan limits must forbid extension types");
  }
  if (scan.float32Allowed !== false) {
    invalid("the scan limits must forbid float32 (f64 is the declared float width)");
  }

  const elements = manifest.envelope?.elements;
  if (!Array.isArray(elements) || elements.length !== 7) {
    invalid("the envelope must be an array of exactly seven elements");
  }
  elements.forEach((element, index) => {
    if (element.index !== index) {
      invalid(`envelope element ${index} declares index ${element.index}`);
    }
  });
  const expectedEnvelope = ["major", "minor", "features", "tag", "correlation", "causation", "body"];
  elements.forEach((element, index) => {
    if (element.name !== expectedEnvelope[index]) {
      invalid(`envelope element ${index} must be ${expectedEnvelope[index]}, not ${element.name}`);
    }
  });
  if (elements[6].type?.kind !== "message_body") {
    invalid("envelope element 6 must be the message body");
  }
  for (const index of [0, 1, 2, 3]) {
    if (elements[index].type?.kind !== "u32") {
      invalid(`envelope element ${index} must be u32`);
    }
  }
  for (const index of [4, 5]) {
    if (elements[index].type?.kind !== "u64") {
      invalid(`envelope element ${index} must be u64`);
    }
  }

  // Named types: strictly append-ordered, refs point backwards only (this
  // keeps the manifest acyclic and the emission order valid; a forward or
  // cyclic reference is a manifest error).
  if (!Array.isArray(manifest.types)) {
    invalid("types must be an array");
  }
  const seenTypeNames = new Set();
  for (const entry of manifest.types) {
    const forms = Object.keys(entry ?? {});
    if (forms.length !== 1) {
      invalid("every named type declares exactly one form");
    }
    const form = forms[0];
    const definition = entry[form];
    checkName(definition.name, `${form} type`);
    if (reservedTypeNames.has(definition.name)) {
      invalid(`the type name ${definition.name} is reserved by the generator`);
    }
    if (seenTypeNames.has(definition.name)) {
      invalid(`the type ${definition.name} is declared twice`);
    }
    checkDoc(definition, definition.name);
    if (form === "struct") {
      checkFields(definition.fields, definition.name, false);
    } else if (form === "enum") {
      if (!Array.isArray(definition.values) || definition.values.length === 0) {
        invalid(`enum ${definition.name} must declare values`);
      }
      const values = new Set();
      for (const value of definition.values) {
        checkName(value.name, `${definition.name} value`);
        if (!Number.isInteger(value.value) || value.value < 0 || value.value > 0xffff_ffff) {
          invalid(`enum ${definition.name} value ${value.name} must be a u32`);
        }
        if (values.has(value.value)) {
          invalid(`enum ${definition.name} repeats the value ${value.value}`);
        }
        values.add(value.value);
        checkDoc(value, `${definition.name}.${value.name}`);
      }
    } else if (form === "union") {
      if (!Array.isArray(definition.variants) || definition.variants.length === 0) {
        invalid(`union ${definition.name} must declare variants`);
      }
      const discriminants = new Set();
      for (const variant of definition.variants) {
        // Variant names appear only as string literals (kind discriminants),
        // so reserved words such as "delete" are legal wire names.
        checkName(variant.name, `${definition.name} variant`, true);
        if (!Number.isInteger(variant.discriminant) || variant.discriminant < 0 || variant.discriminant > 0xffff_ffff) {
          invalid(`union ${definition.name} variant ${variant.name} discriminant must be a u32`);
        }
        if (discriminants.has(variant.discriminant)) {
          invalid(`union ${definition.name} repeats discriminant ${variant.discriminant}`);
        }
        discriminants.add(variant.discriminant);
        checkDoc(variant, `${definition.name}.${variant.name}`);
        checkFields(variant.fields, `${definition.name}.${variant.name}`, true);
      }
    } else {
      invalid(`the named type form ${form} is unknown`);
    }
    declaredTypeNames.add(definition.name);
    seenTypeNames.add(definition.name);
  }

  if (!Array.isArray(manifest.messages) || manifest.messages.length === 0) {
    invalid("messages must be a non-empty array");
  }
  const tags = new Set();
  const messageNames = new Set();
  for (const message of manifest.messages) {
    checkName(message.name, "message");
    if (messageNames.has(message.name)) {
      invalid(`the message ${message.name} is declared twice`);
    }
    messageNames.add(message.name);
    if (!Number.isInteger(message.tag) || message.tag < 1 || message.tag > 0xffff_ffff) {
      invalid(`message ${message.name} tag must be a non-zero u32`);
    }
    if (tags.has(message.tag)) {
      invalid(`message tag ${message.tag} is declared twice; tags are never reused`);
    }
    tags.add(message.tag);
    if (!["client_to_server", "server_to_client", "bidirectional"].includes(message.direction)) {
      invalid(`message ${message.name} has the unknown direction ${message.direction}`);
    }
    checkDoc(message, message.name);
    checkFields(message.fields, message.name, true);
  }
}

// --- Naming -------------------------------------------------------------------

function pascal(name) {
  return name.split("_").map((part) => (part === "" ? "" : part[0].toUpperCase() + part.slice(1))).join("");
}

const typePrefix = "TerrascaleSyncV2Msgpack";
const constPrefix = "terrascaleSyncV2Msgpack";

function structTypeName(name) {
  return `${typePrefix}${pascal(name)}`;
}

function enumTypeName(name) {
  return `${typePrefix}${pascal(name)}Name`;
}

function namedTypeName(definition) {
  return definition.form === "enum" ? enumTypeName(definition.name) : structTypeName(definition.name);
}

function encodeFnName(name) {
  return `msgpackEncode${pascal(name)}`;
}

function decodeFnName(name) {
  return `msgpackDecode${pascal(name)}`;
}

function validateFnName(name) {
  return `validate${typePrefix}${pascal(name)}`;
}

function predicateFnName(name) {
  return `msgpackIs${pascal(name)}`;
}

function enumCodesConstName(name) {
  return `${constPrefix}${pascal(name)}Codes`;
}

function enumNamesConstName(name) {
  return `${constPrefix}${pascal(name)}Names`;
}

// --- Type model ---------------------------------------------------------------

const declaredTypeNames = new Set();
const typeForms = new Map();

function typeModel() {
  for (const entry of manifest.types) {
    const form = Object.keys(entry)[0];
    typeForms.set(entry[form].name, { form, ...entry[form] });
  }
  const checkAggregateFields = (fields, owner) => {
    for (const field of fields) {
      const aggregate = field.type.kind === "array" ? field.type.aggregateBytes : undefined;
      if (aggregate === undefined) continue;
      const itemType = typeOf(field.type.items);
      if (itemType?.form !== "struct") {
        invalid(`${owner}.${field.name}: aggregateBytes requires an array of a structured type`);
      }
      const aggregateField = itemType.fields.find((item) => item.name === aggregate.field);
      if (aggregateField === undefined || !isBinaryOrOptionalBinary(aggregateField.type)) {
        invalid(`${owner}.${field.name}: aggregateBytes field ${aggregate.field} must be binary or optional binary`);
      }
    }
  };
  for (const definition of typeForms.values()) {
    if (definition.form === "struct") {
      checkAggregateFields(definition.fields, definition.name);
    } else if (definition.form === "union") {
      for (const variant of definition.variants) {
        checkAggregateFields(variant.fields, `${definition.name}.${variant.name}`);
      }
    }
  }
  for (const message of manifest.messages) {
    checkAggregateFields(message.fields, message.name);
  }
}

function isBinaryOrOptionalBinary(type) {
  return type.kind === "binary" || (type.kind === "option" && type.of.kind === "binary");
}

function typeOf(type) {
  if (type.kind === "ref") {
    return typeForms.get(type.name);
  }
  return null;
}

function collectKinds(type, into) {
  if (!type) return;
  into.add(type.kind);
  if (type.kind === "array") collectKinds(type.items, into);
  if (type.kind === "option") collectKinds(type.of, into);
  if (type.kind === "tuple") for (const element of type.of) collectKinds(element, into);
  if (type.kind === "ref") into.add(`ref:${typeForms.get(type.name).form}`);
}

function tsTypeText(type) {
  switch (type.kind) {
    case "u32":
    case "f64":
      return "number";
    case "u64":
    case "i64":
      return "bigint";
    case "bool":
      return "boolean";
    case "text":
      return "string";
    case "binary":
      return "Uint8Array";
    case "option":
      return `${tsTypeText(type.of)} | null`;
    case "array":
      return `ReadonlyArray<${tsTypeText(type.items)}>`;
    case "tuple":
      return `readonly [${type.of.map((element) => tsTypeText(element)).join(", ")}]`;
    case "ref":
      return namedTypeName(typeOf(type));
    default:
      throw new Error(`unknown type kind ${type.kind}`);
  }
}

// --- Emission: validators -----------------------------------------------------

const u32MaxText = "4294967295";
const u64MaxText = "18446744073709551615n";
const i64MinText = "-9223372036854775808n";
const i64MaxText = "9223372036854775807n";

function predicateCall(type, expr) {
  switch (type.kind) {
    case "u32":
      return `msgpackIsU32(${expr})`;
    case "u64":
      return `msgpackIsU64(${expr})`;
    case "i64":
      return `msgpackIsI64(${expr})`;
    case "f64":
      return `msgpackIsF64(${expr})`;
    case "bool":
      return `msgpackIsBool(${expr})`;
    case "text":
      return `msgpackIsText(${expr}, ${type.minBytes ?? 0}, ${type.maxBytes})`;
    case "binary":
      return `msgpackIsBinary(${expr}, ${type.minBytes}, ${type.maxBytes})`;
    case "ref":
      if (typeOf(type).form === "enum") {
        return `${predicateFnName(type.name)}(${expr})`;
      }
      return null;
    default:
      return null;
  }
}

function aggregateVariable(path) {
  return `aggregate${path.replace(/[^a-z0-9]+/giu, "_").split("_").filter(Boolean).map(pascal).join("")}Bytes`;
}

function emitAggregateCheck(type, expr, path, indent, out, error) {
  if (type.aggregateBytes === undefined) return;
  const variable = aggregateVariable(path);
  const item = `${variable}Item`;
  const value = `${variable}Value`;
  out.push(
    `${pad(indent)}let ${variable} = 0;`,
    `${pad(indent)}for (const ${item} of ${expr}) {`,
    `${pad(indent + 1)}const ${value} = ${item}.${type.aggregateBytes.field};`,
    `${pad(indent + 1)}if (${value} instanceof Uint8Array) ${variable} += ${value}.length;`,
    `${pad(indent)}}`,
    `${pad(indent)}if (${variable} > ${type.aggregateBytes.maxBytes}) {`,
    `${pad(indent + 1)}${error};`,
    `${pad(indent)}}`,
  );
}

const pad = (indent) => "  ".repeat(indent);

function emitValidate(type, expr, path, indent, out) {
  const direct = predicateCall(type, expr);
  if (direct !== null) {
    out.push(`${pad(indent)}if (!${direct}) {`);
    out.push(`${pad(indent + 1)}throw msgpackInvalid("${path}");`);
    out.push(`${pad(indent)}}`);
    return;
  }
  switch (type.kind) {
    case "option": {
      out.push(`${pad(indent)}if (${expr} !== null) {`);
      emitValidate(type.of, expr, path, indent + 1, out);
      out.push(`${pad(indent)}}`);
      return;
    }
    case "array": {
      out.push(`${pad(indent)}if (!Array.isArray(${expr}) || ${expr}.length > ${type.maxItems}) {`);
      out.push(`${pad(indent + 1)}throw msgpackInvalid("${path}");`);
      out.push(`${pad(indent)}}`);
      out.push(`${pad(indent)}for (const element of ${expr}) {`);
      emitValidate(type.items, "element", `${path}[]`, indent + 1, out);
      out.push(`${pad(indent)}}`);
      emitAggregateCheck(type, expr, path, indent, out, `throw msgpackInvalid("${path}.aggregateBytes")`);
      return;
    }
    case "tuple": {
      out.push(`${pad(indent)}if (!Array.isArray(${expr}) || ${expr}.length !== ${type.of.length}) {`);
      out.push(`${pad(indent + 1)}throw msgpackInvalid("${path}");`);
      out.push(`${pad(indent)}}`);
      type.of.forEach((element, index) => {
        emitValidate(element, `${expr}[${index}]`, `${path}[${index}]`, indent, out);
      });
      return;
    }
    case "ref": {
      out.push(`${pad(indent)}${validateFnName(type.name)}(${expr});`);
      return;
    }
    default:
      throw new Error(`cannot validate kind ${type.kind}`);
  }
}

// --- Emission: encoders -------------------------------------------------------

function emitEncode(type, expr, indent, out, depth) {
  const element = depth === 0 ? "element" : `element${depth}`;
  switch (type.kind) {
    case "u32":
      out.push(`${pad(indent)}writer.u32(${expr});`);
      return;
    case "u64":
      out.push(`${pad(indent)}writer.u64(${expr});`);
      return;
    case "i64":
      out.push(`${pad(indent)}writer.i64(${expr});`);
      return;
    case "f64":
      out.push(`${pad(indent)}writer.f64(${expr});`);
      return;
    case "bool":
      out.push(`${pad(indent)}writer.bool(${expr});`);
      return;
    case "text":
      out.push(`${pad(indent)}writer.text(${expr}, ${type.maxBytes});`);
      return;
    case "binary":
      out.push(`${pad(indent)}writer.binary(${expr}, ${type.minBytes}, ${type.maxBytes});`);
      return;
    case "option":
      out.push(`${pad(indent)}if (${expr} === null) {`);
      out.push(`${pad(indent + 1)}writer.nil();`);
      out.push(`${pad(indent)}} else {`);
      emitEncode(type.of, expr, indent + 1, out, depth);
      out.push(`${pad(indent)}}`);
      return;
    case "array":
      out.push(`${pad(indent)}writer.arrayHeader(${expr}.length);`);
      out.push(`${pad(indent)}for (const ${element} of ${expr}) {`);
      emitEncode(type.items, element, indent + 1, out, depth + 1);
      out.push(`${pad(indent)}}`);
      return;
    case "tuple":
      out.push(`${pad(indent)}writer.arrayHeader(${type.of.length});`);
      type.of.forEach((element2, index) => {
        emitEncode(element2, `${expr}[${index}]`, indent, out, depth);
      });
      return;
    case "ref":
      out.push(`${pad(indent)}${encodeFnName(type.name)}(writer, ${expr});`);
      return;
    default:
      throw new Error(`cannot encode kind ${type.kind}`);
  }
}

// --- Emission: decoders -------------------------------------------------------

function decodeExpression(type) {
  switch (type.kind) {
    case "u32":
      return "reader.u32()";
    case "u64":
      return "reader.u64()";
    case "i64":
      return "reader.i64()";
    case "f64":
      return "reader.f64()";
    case "bool":
      return "reader.bool()";
    case "text":
      return `reader.text(${type.maxBytes})`;
    case "binary":
      return `reader.binary(${type.minBytes}, ${type.maxBytes})`;
    case "ref":
      return `${decodeFnName(type.name)}(reader)`;
    default:
      return null;
  }
}

function emitDecode(type, name, path, indent, out) {
  const expression = decodeExpression(type);
  if (expression !== null) {
    out.push(`${pad(indent)}const ${name} = ${expression};`);
    if (type.kind === "text" && (type.minBytes ?? 0) > 0) {
      out.push(
        `${pad(indent)}if (msgpackUtf8ByteLength(${name}) < ${type.minBytes}) {`,
        `${pad(indent + 1)}throw new TerrascaleMsgpackError("invalid_shape", "${path} is below its minimum byte bound");`,
        `${pad(indent)}}`,
      );
    }
    return;
  }
  switch (type.kind) {
    case "option": {
      const inner = decodeExpression(type.of);
      if (inner !== null) {
        out.push(`${pad(indent)}const ${name} = reader.optionNil() ? null : ${inner};`);
        return;
      }
      out.push(`${pad(indent)}let ${name}: ${tsTypeText(type)} | null = null;`);
      out.push(`${pad(indent)}if (!reader.optionNil()) {`);
      emitDecode(type.of, name, path, indent + 1, out);
      out.push(`${pad(indent)}}`);
      return;
    }
    case "array": {
      const itemExpression = decodeExpression(type.items);
      out.push(`${pad(indent)}const ${name}Count = reader.arrayCount(${type.maxItems});`);
      out.push(`${pad(indent)}const ${name}: Array<${tsTypeText(type.items)}> = [];`);
      out.push(`${pad(indent)}for (let index = 0; index < ${name}Count; index++) {`);
      if (itemExpression !== null) {
        out.push(`${pad(indent + 1)}${name}.push(${itemExpression});`);
      } else {
        emitDecode(type.items, `${name}Item`, `${path}[]`, indent + 1, out);
        out.push(`${pad(indent + 1)}${name}.push(${name}Item);`);
      }
      out.push(`${pad(indent)}}`);
      emitAggregateCheck(type, name, path, indent, out, `throw new TerrascaleMsgpackError("limit_binary", "${path} exceeds its aggregate binary byte bound")`);
      return;
    }
    case "tuple": {
      out.push(`${pad(indent)}reader.arrayHeader(${type.of.length});`);
      const partNames = type.of.map((_, index) => `${name}Part${index}`);
      type.of.forEach((element, index) => {
        emitDecode(element, partNames[index], `${path}[${index}]`, indent, out);
      });
      out.push(`${pad(indent)}const ${name}: ${tsTypeText(type)} = [${partNames.join(", ")}];`);
      return;
    }
    default:
      throw new Error(`cannot decode kind ${type.kind}`);
  }
}

// --- Emission: the file -------------------------------------------------------

function generate(manifestText, manifestSha256, generatorSha256) {
  typeModel();
  const kinds = new Set();
  for (const definition of typeForms.values()) {
    if (definition.form === "struct") {
      for (const field of definition.fields) collectKinds(field.type, kinds);
    } else if (definition.form === "union") {
      for (const variant of definition.variants) {
        for (const field of variant.fields) collectKinds(field.type, kinds);
      }
    }
  }
  for (const message of manifest.messages) {
    for (const field of message.fields) collectKinds(field.type, kinds);
  }
  kinds.add("u32"); // the envelope carries four u32 elements.
  const usesText = kinds.has("text");

  const lines = [];
  const push = (...items) => lines.push(...items);
  const messageKindUnionName = `${typePrefix}MessageKindName`;
  const messageUnionName = `${typePrefix}Message`;
  const envelopeName = `${typePrefix}Envelope`;

  // Header and imports.
  push(
    "// GENERATED FILE - DO NOT EDIT.",
    "//",
    "// Source of authority: src/msgpack/manifest/terrascale.sync.v2.msgpack.json",
    "// Generator: scripts/generate-msgpack-protocol.ts (ADR 0025: the public",
    "// terrascale.sync.v2.msgpack WebSocket protocol is generated from one",
    "// checked-in versioned manifest; this TypeScript half provides codecs,",
    "// exact-shape validators, and public types for @terrascale/sdk).",
    "//",
    `// manifest sha256:   ${manifestSha256}`,
    `// generator sha256:  ${generatorSha256}`,
    "//",
    "// Regenerate: node scripts/generate-msgpack-protocol.ts",
    "// Drift gate:  node scripts/generate-msgpack-protocol.ts --check",
    "// Drift test:  npm test (test/msgpack-manifest-drift.test.ts)",
    "",
  );

  const imports = ["MsgpackReader", "MsgpackWriter", "scanMessagePack", "TerrascaleMsgpackError"];
  if (usesText) imports.push("msgpackTextIsScalar", "msgpackUtf8ByteLength");
  const sortedImports = [...imports].sort();
  push(`import {`);
  for (const name of sortedImports.slice(0, -1)) push(`  ${name},`);
  push(`  ${sortedImports[sortedImports.length - 1]},`);
  push(`  type TerrascaleMsgpackScanLimits,`);
  push(`} from "../runtime/bounded-msgpack.js";`);
  push("");

  // Runtime-independent module helpers.
  push(
    "function msgpackInvalid(path: string): TerrascaleMsgpackError {",
    `  return new TerrascaleMsgpackError("invalid_shape", \`\${path} does not match the manifest shape\`);`,
    "}",
    "",
    "function msgpackShape(path: string): TerrascaleMsgpackError {",
    `  return new TerrascaleMsgpackError("invalid_shape", \`\${path} does not carry its declared shape\`);`,
    "}",
    "",
  );
  if (kinds.has("u32")) {
    push(
      "function msgpackIsU32(value: unknown): value is number {",
      `  return typeof value === "number" && Number.isInteger(value) && value >= 0 && value <= ${u32MaxText};`,
      "}",
      "",
    );
  }
  if (kinds.has("u64")) {
    push(
      "function msgpackIsU64(value: unknown): value is bigint {",
      `  return typeof value === "bigint" && value >= 0n && value <= ${u64MaxText};`,
      "}",
      "",
    );
  }
  if (kinds.has("i64")) {
    push(
      "function msgpackIsI64(value: unknown): value is bigint {",
      `  return typeof value === "bigint" && value >= ${i64MinText} && value <= ${i64MaxText};`,
      "}",
      "",
    );
  }
  if (kinds.has("f64")) {
    push(
      "function msgpackIsF64(value: unknown): value is number {",
      `  return typeof value === "number";`,
      "}",
      "",
    );
  }
  if (kinds.has("bool")) {
    push(
      "function msgpackIsBool(value: unknown): value is boolean {",
      `  return typeof value === "boolean";`,
      "}",
      "",
    );
  }
  if (usesText) {
    push(
      "function msgpackIsText(value: unknown, minBytes: number, maxBytes: number): value is string {",
      "  const bytes = typeof value === \"string\" ? msgpackUtf8ByteLength(value) : -1;",
      "  return typeof value === \"string\" && msgpackTextIsScalar(value) && bytes >= minBytes && bytes <= maxBytes;",
      "}",
      "",
    );
  }
  if (kinds.has("binary")) {
    push(
      "function msgpackIsBinary(value: unknown, minBytes: number, maxBytes: number): value is Uint8Array {",
      "  return value instanceof Uint8Array && value.length >= minBytes && value.length <= maxBytes;",
      "}",
      "",
    );
  }

  // Identity and limits constants.
  push(
    `/** The wire identity of the generated protocol. */`,
    `export const ${constPrefix}Protocol = ${JSON.stringify(manifest.protocol)} as const;`,
    "",
    `/** The manifest revision this file was generated from. */`,
    `export const ${constPrefix}ManifestVersion = ${manifest.manifestVersion} as const;`,
    "",
    `/** The SHA-256 of the manifest file this file was generated from. */`,
    `export const ${constPrefix}ManifestSha256 = ${JSON.stringify(manifestSha256)} as const;`,
    "",
    `/** The protocol major this build speaks; anything else fails closed. */`,
    `export const ${constPrefix}MajorVersion = ${manifest.protocolMajor} as const;`,
    "",
    `/** The protocol minor this build speaks; anything else fails closed. */`,
    `export const ${constPrefix}MinorVersion = ${manifest.protocolMinor} as const;`,
    "",
    `/** The required feature bits this build understands; unknown bits fail closed. */`,
    `export const ${constPrefix}SupportedFeatures = ${manifest.supportedFeatures} as const;`,
    "",
    `/** The whole-message ceiling (bytes), enforced before any byte parses. */`,
    `export const ${constPrefix}MaxMessageBytes = ${manifest.scan.maxMessageBytes} as const;`,
    "",
    `/** The allocation-bounded structural scan limits, generated from the manifest. */`,
    `export const ${constPrefix}ScanLimits: Readonly<TerrascaleMsgpackScanLimits> = Object.freeze({`,
    `  maxMessageBytes: ${manifest.scan.maxMessageBytes},`,
    `  maxDepth: ${manifest.scan.maxDepth},`,
    `  maxStringBytes: ${manifest.scan.maxStringBytes},`,
    `  maxBinaryBytes: ${manifest.scan.maxBinaryBytes},`,
    `  maxArrayItems: ${manifest.scan.maxArrayItems},`,
    `  maxMapEntries: ${manifest.scan.maxMapEntries},`,
    `  extensionsAllowed: ${manifest.scan.extensionsAllowed},`,
    `  float32Allowed: ${manifest.scan.float32Allowed},`,
    `});`,
    "",
  );

  // Direction type.
  push(
    `/** The direction one message may travel. */`,
    `export type ${typePrefix}MessageDirection = "client_to_server" | "server_to_client" | "bidirectional";`,
    "",
  );

  // Message tag tables.
  push(
    `/** The message kind names, in manifest order. */`,
    `export type ${messageKindUnionName} =`,
    ...manifest.messages.map(
      (message, index) =>
        `  | ${JSON.stringify(message.name)}${index === manifest.messages.length - 1 ? ";" : ""}`,
    ),
    "",
    `/** The manifest message tags; tags are stable and never reused. */`,
    `export const ${constPrefix}MessageTags: Readonly<Record<${messageKindUnionName}, number>> = Object.freeze({`,
    ...manifest.messages.map((message) => `  ${message.name}: ${message.tag},`),
    `});`,
    "",
    `/** The reverse tag table; unknown tags fail closed on decode. */`,
    `export const ${constPrefix}MessageNames: Readonly<Record<number, ${messageKindUnionName}>> = Object.freeze({`,
    ...manifest.messages.map((message) => `  ${message.tag}: ${JSON.stringify(message.name)},`),
    `});`,
    "",
    `/** The manifest-recorded travel direction of each message. */`,
    `export const ${constPrefix}MessageDirections: Readonly<Record<${messageKindUnionName}, ${typePrefix}MessageDirection>> = Object.freeze({`,
    ...manifest.messages.map((message) => `  ${message.name}: ${JSON.stringify(message.direction)},`),
    `});`,
    "",
  );

  // Named types: type declarations.
  for (const definition of typeForms.values()) {
    if (definition.form === "struct") {
      push(
        `/** ${definition.doc ?? `The ${definition.name} structured type.`} (indexed array of ${definition.fields.length} fields) */`,
        `export type ${structTypeName(definition.name)} = {`,
        ...definition.fields.map(
          (field) => `  readonly ${field.name}: ${tsTypeText(field.type)};`,
        ),
        `};`,
        "",
      );
    } else if (definition.form === "enum") {
      push(
        `/** ${definition.doc ?? `The ${definition.name} vocabulary.`} */`,
        `export type ${enumTypeName(definition.name)} =`,
        ...definition.values.map(
          (value, index) =>
            `  | ${JSON.stringify(value.name)}${index === definition.values.length - 1 ? ";" : ""}`,
        ),
        "",
      );
    } else {
      push(
        `/** ${definition.doc ?? `The ${definition.name} union.`} (tagged indexed array: [discriminant, fields...]) */`,
        `export type ${structTypeName(definition.name)} =`,
        ...definition.variants.map((variant) => {
          const fields = variant.fields
            .map((field) => `readonly ${field.name}: ${tsTypeText(field.type)};`)
            .join(" ");
          const members = [`readonly kind: ${JSON.stringify(variant.name)};`];
          if (fields !== "") members.push(fields);
          return `  | { ${members.join(" ")} }`;
        }),
        `;`,
        "",
      );
    }
  }

  // The message union.
  push(
    `/** One public terrascale.sync.v2.msgpack message body (the tagged member of the envelope). */`,
    `export type ${messageUnionName} =`,
    ...manifest.messages.map((message) => {
      const fields = message.fields
        .map((field) => `readonly ${field.name}: ${tsTypeText(field.type)};`)
        .join(" ");
      const members = [`readonly kind: ${JSON.stringify(message.name)};`];
      if (fields !== "") members.push(fields);
      return `  | { ${members.join(" ")} }`;
    }),
    `;`,
    "",
    `/** One sealed protocol message: the envelope identities plus the tagged body. */`,
    `export type ${envelopeName} = {`,
    `  readonly features: number;`,
    `  readonly correlation: bigint;`,
    `  readonly causation: bigint;`,
    `  readonly message: ${messageUnionName};`,
    `};`,
    "",
  );

  // Enum records and predicates.
  for (const definition of typeForms.values()) {
    if (definition.form !== "enum") continue;
    const typeName = enumTypeName(definition.name);
    push(
      `const ${enumCodesConstName(definition.name)}: Readonly<Record<${typeName}, number>> = Object.freeze({`,
      ...definition.values.map((value) => `  ${value.name}: ${value.value},`),
      `});`,
      ``,
      `const ${enumNamesConstName(definition.name)}: Readonly<Record<number, ${typeName}>> = Object.freeze({`,
      ...definition.values.map((value) => `  ${value.value}: ${JSON.stringify(value.name)},`),
      `});`,
      ``,
      `function ${predicateFnName(definition.name)}(value: unknown): value is ${typeName} {`,
      ...definition.values.map((value) => `  if (value === ${JSON.stringify(value.name)}) { return true; }`),
      `  return false;`,
      `}`,
      ``,
    );
  }

  // Validators for named types.
  for (const definition of typeForms.values()) {
    if (definition.form === "struct") {
      const body = [];
      body.push(`  if (typeof candidate !== "object" || candidate === null`);
      for (const field of definition.fields) body.push(`    || !(${JSON.stringify(field.name)} in candidate)`);
      body.push(`  ) {`);
      body.push(`    throw msgpackInvalid("${definition.name}");`);
      body.push(`  }`);
      body.push(`  if (Object.keys(candidate).length !== ${definition.fields.length}) {`);
      body.push(`    throw msgpackInvalid("${definition.name}");`);
      body.push(`  }`);
      for (const field of definition.fields) {
        body.push(`  const ${field.name} = (candidate as { ${field.name}: unknown }).${field.name};`);
        emitValidate(field.type, field.name, `${definition.name}.${field.name}`, 1, body);
      }
      push(
        `/** The exact-shape validator for the ${definition.name} structured type. */`,
        `export function ${validateFnName(definition.name)}(candidate: unknown): void {`,
        ...body,
        `}`,
        ``,
      );
    } else if (definition.form === "union") {
      const body = [];
      body.push(`  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {`);
      body.push(`    throw msgpackInvalid("${definition.name}");`);
      body.push(`  }`);
      body.push(`  const kind = (candidate as { kind: unknown }).kind;`);
      for (const variant of definition.variants) {
        body.push(`  if (kind === ${JSON.stringify(variant.name)}) {`);
        body.push(`    if (Object.keys(candidate).length !== ${1 + variant.fields.length}) {`);
        body.push(`      throw msgpackInvalid("${definition.name}.${variant.name}");`);
        body.push(`    }`);
        if (variant.fields.length === 0) {
          body.push(`    return;`);
        } else {
          for (const field of variant.fields) {
            body.push(
              `    if (!(${JSON.stringify(field.name)} in candidate)) {`,
              `      throw msgpackInvalid("${definition.name}.${variant.name}");`,
              `    }`,
              `    const ${field.name} = (candidate as { ${field.name}: unknown }).${field.name};`,
            );
            emitValidate(
              field.type,
              field.name,
              `${definition.name}.${variant.name}.${field.name}`,
              2,
              body,
            );
          }
          body.push(`    return;`);
        }
        body.push(`  }`);
      }
      body.push(`  throw msgpackInvalid("${definition.name}.kind");`);
      push(
        `/** The exact-shape validator for the ${definition.name} union. */`,
        `export function ${validateFnName(definition.name)}(candidate: unknown): void {`,
        ...body,
        `}`,
        ``,
      );
    }
  }

  // The message validator.
  const messageValidatorBody = [];
  messageValidatorBody.push(`  if (typeof candidate !== "object" || candidate === null || !("kind" in candidate)) {`);
  messageValidatorBody.push(`    throw msgpackInvalid("message");`);
  messageValidatorBody.push(`  }`);
  messageValidatorBody.push(`  const kind = (candidate as { kind: unknown }).kind;`);
  for (const message of manifest.messages) {
    messageValidatorBody.push(`  if (kind === ${JSON.stringify(message.name)}) {`);
    messageValidatorBody.push(`    if (Object.keys(candidate).length !== ${1 + message.fields.length}) {`);
    messageValidatorBody.push(`      throw msgpackInvalid("${message.name}");`);
    messageValidatorBody.push(`    }`);
    for (const field of message.fields) {
      messageValidatorBody.push(
        `    if (!(${JSON.stringify(field.name)} in candidate)) {`,
        `      throw msgpackInvalid("${message.name}");`,
        `    }`,
        `    const ${field.name} = (candidate as { ${field.name}: unknown }).${field.name};`,
      );
      emitValidate(field.type, field.name, `${message.name}.${field.name}`, 2, messageValidatorBody);
    }
    messageValidatorBody.push(`    return;`);
    messageValidatorBody.push(`  }`);
  }
  messageValidatorBody.push(`  throw msgpackInvalid("message.kind");`);
  push(
    `/** The exact-shape validator for one public message body (untrusted in-memory values too). */`,
    `export function validate${typePrefix}Message(candidate: unknown): void {`,
    ...messageValidatorBody,
    `}`,
    ``,
  );

  // Envelope validator.
  push(
    `/** The exact-shape validator for one sealed protocol message. */`,
    `export function validate${typePrefix}Envelope(envelope: unknown): void {`,
    `  if (typeof envelope !== "object" || envelope === null`,
    `    || !("features" in envelope) || !("correlation" in envelope)`,
    `    || !("causation" in envelope) || !("message" in envelope)) {`,
    `    throw msgpackInvalid("envelope");`,
    `  }`,
    `  if (Object.keys(envelope).length !== 4) {`,
    `    throw msgpackInvalid("envelope");`,
    `  }`,
    `  const candidate = envelope as { features: unknown; correlation: unknown; causation: unknown; message: unknown };`,
    `  if (!msgpackIsU32(candidate.features)) {`,
    `    throw msgpackInvalid("envelope.features");`,
    `  }`,
    `  if (!msgpackIsU64(candidate.correlation)) {`,
    `    throw msgpackInvalid("envelope.correlation");`,
    `  }`,
    `  if (!msgpackIsU64(candidate.causation)) {`,
    `    throw msgpackInvalid("envelope.causation");`,
    `  }`,
    `  if ((candidate.features & ~${constPrefix}SupportedFeatures) !== 0) {`,
    `    throw new TerrascaleMsgpackError("unknown_feature", "the envelope requires feature bits this build does not speak");`,
    `  }`,
    `  validate${typePrefix}Message(candidate.message);`,
    `}`,
    ``,
  );

  // Encoders for named types.
  for (const definition of typeForms.values()) {
    if (definition.form === "struct") {
      const body = [`  writer.arrayHeader(${definition.fields.length});`];
      for (const field of definition.fields) {
        emitEncode(field.type, `value.${field.name}`, 1, body, 0);
      }
      push(
        `function ${encodeFnName(definition.name)}(writer: MsgpackWriter, value: ${structTypeName(definition.name)}): void {`,
        ...body,
        `}`,
        ``,
      );
    } else if (definition.form === "union") {
      const body = [`  switch (value.kind) {`];
      for (const variant of definition.variants) {
        body.push(`    case ${JSON.stringify(variant.name)}: {`);
        body.push(`      writer.arrayHeader(${1 + variant.fields.length});`);
        body.push(`      writer.u32(${variant.discriminant});`);
        for (const field of variant.fields) {
          emitEncode(field.type, `value.${field.name}`, 3, body, 0);
        }
        body.push(`      break;`);
        body.push(`    }`);
      }
      body.push(`  }`);
      push(
        `function ${encodeFnName(definition.name)}(writer: MsgpackWriter, value: ${structTypeName(definition.name)}): void {`,
        ...body,
        `}`,
        ``,
      );
    } else {
      push(
        `function ${encodeFnName(definition.name)}(writer: MsgpackWriter, value: ${enumTypeName(definition.name)}): void {`,
        `  writer.u32(${enumCodesConstName(definition.name)}[value]);`,
        `}`,
        ``,
      );
    }
  }

  // The message body encoder (single switch; every case returns or breaks).
  const bodyEncoder = [];
  bodyEncoder.push(`  switch (message.kind) {`);
  for (const message of manifest.messages) {
    bodyEncoder.push(`    case ${JSON.stringify(message.name)}: {`);
    bodyEncoder.push(`      writer.arrayHeader(${message.fields.length});`);
    for (const field of message.fields) {
      emitEncode(field.type, `message.${field.name}`, 3, bodyEncoder, 0);
    }
    bodyEncoder.push(`      break;`);
    bodyEncoder.push(`    }`);
  }
  bodyEncoder.push(`  }`);
  push(
    `function ${encodeFnName("MessageBody")}(writer: MsgpackWriter, message: ${messageUnionName}): void {`,
    ...bodyEncoder,
    `}`,
    ``,
    `/** Encodes one message body as its indexed array (the envelope body element). */`,
    `export function encode${typePrefix}Message(message: ${messageUnionName}): Uint8Array {`,
    `  validate${typePrefix}Message(message);`,
    `  const writer = new MsgpackWriter();`,
    `  ${encodeFnName("MessageBody")}(writer, message);`,
    `  return writer.finish(${constPrefix}MaxMessageBytes);`,
    `}`,
    ``,
    `/** Encodes one complete protocol message (envelope plus body). */`,
    `export function encode${typePrefix}Envelope(envelope: ${envelopeName}): Uint8Array {`,
    `  validate${typePrefix}Envelope(envelope);`,
    `  const writer = new MsgpackWriter();`,
    `  writer.arrayHeader(7);`,
    `  writer.u32(${constPrefix}MajorVersion);`,
    `  writer.u32(${constPrefix}MinorVersion);`,
    `  writer.u32(envelope.features);`,
    `  writer.u32(${constPrefix}MessageTags[envelope.message.kind]);`,
    `  writer.u64(envelope.correlation);`,
    `  writer.u64(envelope.causation);`,
    `  ${encodeFnName("MessageBody")}(writer, envelope.message);`,
    `  return writer.finish(${constPrefix}MaxMessageBytes);`,
    `}`,
    ``,
  );

  // Decoders for named types.
  for (const definition of typeForms.values()) {
    if (definition.form === "struct") {
      const body = [`  reader.arrayHeader(${definition.fields.length});`];
      for (const field of definition.fields) {
        emitDecode(field.type, field.name, `${definition.name}.${field.name}`, 1, body);
      }
      body.push(`  return { ${definition.fields.map((field) => field.name).join(", ")} };`);
      push(
        `function ${decodeFnName(definition.name)}(reader: MsgpackReader): ${structTypeName(definition.name)} {`,
        ...body,
        `}`,
        ``,
      );
    } else if (definition.form === "union") {
      const maxVariantFields = Math.max(...definition.variants.map((variant) => variant.fields.length));
      const body = [];
      body.push(`  const count = reader.arrayCount(${1 + maxVariantFields});`);
      body.push(`  const discriminant = reader.u32();`);
      for (const variant of definition.variants) {
        body.push(`  if (discriminant === ${variant.discriminant}) {`);
        body.push(`    if (count !== ${1 + variant.fields.length}) {`);
        body.push(`      throw msgpackShape("${definition.name}.${variant.name}");`);
        body.push(`    }`);
        for (const field of variant.fields) {
          emitDecode(field.type, field.name, `${definition.name}.${variant.name}.${field.name}`, 3, body);
        }
        body.push(
          `    return { kind: ${JSON.stringify(variant.name)}${variant.fields.length > 0 ? `, ${variant.fields.map((field) => field.name).join(", ")}` : ""} };`,
        );
        body.push(`  }`);
      }
      body.push(
        `  throw new TerrascaleMsgpackError("unknown_variant", \`${definition.name} discriminant \${discriminant} is not defined by the manifest\`);`,
      );
      push(
        `function ${decodeFnName(definition.name)}(reader: MsgpackReader): ${structTypeName(definition.name)} {`,
        ...body,
        `}`,
        ``,
      );
    } else {
      push(
        `function ${decodeFnName(definition.name)}(reader: MsgpackReader): ${enumTypeName(definition.name)} {`,
        `  const code = reader.u32();`,
        `  const name = ${enumNamesConstName(definition.name)}[code];`,
        `  if (name === undefined) {`,
        `    throw new TerrascaleMsgpackError("unknown_enum", \`${definition.name} value \${code} is not defined by the manifest\`);`,
        `  }`,
        `  return name;`,
        `}`,
        ``,
      );
    }
  }

  // The message body decoder and the envelope decode pipeline.
  const bodyDecoder = [];
  bodyDecoder.push(`  switch (tag) {`);
  for (const message of manifest.messages) {
    bodyDecoder.push(`    case ${message.tag}: {`);
    bodyDecoder.push(`      reader.arrayHeader(${message.fields.length});`);
    for (const field of message.fields) {
      emitDecode(field.type, field.name, `${message.name}.${field.name}`, 3, bodyDecoder);
    }
    bodyDecoder.push(
      `      return { kind: ${JSON.stringify(message.name)}${message.fields.length > 0 ? `, ${message.fields.map((field) => field.name).join(", ")}` : ""} };`,
    );
    bodyDecoder.push(`    }`);
  }
  bodyDecoder.push(`    default:`);
  bodyDecoder.push(
    `      throw new TerrascaleMsgpackError("unknown_message_tag", \`message tag \${tag} is not defined by the manifest\`);`,
  );
  bodyDecoder.push(`  }`);
  push(
    `function ${decodeFnName("MessageBody")}(reader: MsgpackReader, tag: number): ${messageUnionName} {`,
    ...bodyDecoder,
    `}`,
    ``,
    `/**`,
    ` * Decodes one complete protocol message from untrusted bytes. The`,
    ` * allocation-bounded structural scan runs first; version and feature`,
    ` * checks fail closed before any body field is read; and the exact-shape`,
    ` * decode assembles the public value only after every field validated, so`,
    ` * a hostile message never yields a partially constructed value.`,
    ` */`,
    `export function decode${typePrefix}Envelope(bytes: Uint8Array): ${envelopeName} {`,
    `  scanMessagePack(bytes, ${constPrefix}ScanLimits);`,
    `  const reader = new MsgpackReader(bytes);`,
    `  reader.arrayHeader(7);`,
    `  const major = reader.u32();`,
    `  if (major !== ${constPrefix}MajorVersion) {`,
    `    throw new TerrascaleMsgpackError("unsupported_version", \`protocol major \${major} is not spoken; this build speaks ${manifest.protocolMajor}\`);`,
    `  }`,
    `  const minor = reader.u32();`,
    `  if (minor !== ${constPrefix}MinorVersion) {`,
    `    throw new TerrascaleMsgpackError("unsupported_version", \`protocol minor \${minor} is not spoken; this build speaks ${manifest.protocolMinor}\`);`,
    `  }`,
    `  const features = reader.u32();`,
    `  if ((features & ~${constPrefix}SupportedFeatures) !== 0) {`,
    `    throw new TerrascaleMsgpackError("unknown_feature", "the message requires feature bits this build does not speak");`,
    `  }`,
    `  const tag = reader.u32();`,
    `  const correlation = reader.u64();`,
    `  const causation = reader.u64();`,
    `  const message = ${decodeFnName("MessageBody")}(reader, tag);`,
    `  reader.end();`,
    `  return { features, correlation, causation, message };`,
    `}`,
    ``,
  );

  return lines.join("\n");
}

// --- Entry --------------------------------------------------------------------

async function main() {
  const manifestText = await readFile(manifestPath, "utf8");
  let parsed;
  try {
    parsed = JSON.parse(manifestText);
  } catch (error) {
    throw new Error(`manifest ${manifestPath} is not valid JSON: ${error.message}`);
  }
  validateManifest(parsed);
  const manifestSha256 = createHash("sha256").update(manifestText, "utf8").digest("hex");
  const generatorSource = await readFile(scriptPath, "utf8");
  const generatorSha256 = createHash("sha256").update(generatorSource, "utf8").digest("hex");
  const output = generate(manifestText, manifestSha256, generatorSha256);
  const outputPath = path.join(outDirectory, outputFileName);
  if (checkMode) {
    let committed = null;
    try {
      committed = await readFile(outputPath, "utf8");
    } catch {
      console.error(`msgpack protocol drift: ${path.relative(repositoryRoot, outputPath)} does not exist`);
      process.exitCode = 1;
      return;
    }
    if (committed !== output) {
      console.error(
        `msgpack protocol drift: ${path.relative(repositoryRoot, outputPath)} differs from the generator output`,
      );
      console.error("regenerate with: node scripts/generate-msgpack-protocol.ts");
      process.exitCode = 1;
      return;
    }
    console.log(`msgpack protocol drift check passed (${path.relative(repositoryRoot, outputPath)})`);
    return;
  }
  await writeFile(outputPath, output, "utf8");
  console.log(`generated ${path.relative(repositoryRoot, outputPath)} from ${path.relative(repositoryRoot, manifestPath)}`);
}

try {
  await main();
} catch (error) {
  console.error(`generate-msgpack-protocol: ${error instanceof Error ? error.message : error}`);
  process.exitCode = 1;
}
