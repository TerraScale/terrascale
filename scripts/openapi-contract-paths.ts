import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

/** Only explicit actual owning producer bytes can satisfy source parity. */
export async function currentProducerOpenApi(): Promise<string> {
  return verifiedSource("TERRASCALE_CURRENT_OPENAPI", "sourceOpenApiSha256");
}

export async function requireCurrentContractInputs(): Promise<void> {
  await Promise.all([
    currentProducerOpenApi(),
    verifiedSource("TERRASCALE_ATOMIC_VECTORS", "sourceAtomicVectorsSha256"),
  ]);
}

async function verifiedSource(
  variable: string,
  pinField: string,
): Promise<string> {
  const source = process.env[variable];
  if (source === undefined || !path.isAbsolute(source)) {
    throw new Error(
      `${variable} must name an absolute actual producer source file.`,
    );
  }
  const [bytes, pinBytes] = await Promise.all([
    readFile(source),
    readFile(path.join(root, "sdk-current-contract.json"), "utf8"),
  ]);
  const pin = JSON.parse(pinBytes);
  const digest = createHash("sha256").update(bytes).digest("hex");
  if (digest !== pin[pinField])
    throw new Error(
      `${variable} digest does not match sdk-current-contract.json.`,
    );
  return source;
}

if (
  process.argv[1] !== undefined &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  if (process.argv.slice(2).join(" ") !== "--check-inputs")
    throw new Error("Expected --check-inputs.");
  await requireCurrentContractInputs();
}
