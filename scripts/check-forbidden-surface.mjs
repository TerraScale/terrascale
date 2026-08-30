#!/usr/bin/env node

import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const forbidden = [
  {
    name: "dedicated auth route",
    pattern: /\/api\/v\d+\/(?:auth|account|users\/me\/(?:delete|login-methods))(?:[\/"'`]|$)|\/api\/v\d+\/projects\/\{[^}]+\}\/auth(?:[\/"'`]|$)/u,
  },
  {
    name: "dedicated auth symbol",
    pattern: /\b(?:Auth(?:Me|Token|Session|Client|SignIn|Register|Password|Mfa|External|Email|Profile|Account|Organization|Project)\w*|authBaseUrl|ProjectAuth\w*)\b|\bauth\.\w+/u,
  },
  {
    name: "API-key surface",
    pattern: /\b(?:ApiKey|APIKey|apiKey|apiKeys)(?:\w*)\b/u,
  },
  {
    name: "M2M surface",
    pattern: /\b(?:M2M\w*|m2m)\b/u,
  },
  {
    name: "legacy global queue route",
    pattern: /\/api\/v\d+\/queues(?:[\/"'`:]|$)|\bGlobalQueue\w*/u,
  },
  {
    name: "billing/pricing route",
    pattern: /\/api\/v\d+\/(?:billing|pricing|organizations\/\{[^}]+\}\/billing|auth\/me\/billing)(?:[\/"'`:]|$)/u,
  },
  {
    name: "billing/pricing symbol",
    pattern: /\b(?:Billing\w*|Pricing\w*|billing(?:[A-Z_\."'`]|$)|pricing(?:[A-Z_\."'`]|$))/u,
  },
  {
    name: "payment onboarding route",
    pattern: /\/api\/v\d+\/onboarding\/payment(?:[-\/]|["'`]|$)/u,
  },
  {
    name: "payment onboarding symbol",
    pattern: /\b(?:OnboardingPayment\w*|CreateOnboardingPayment\w*|ValidateOnboardingPayment\w*|RegisterOnboardingPayment\w*|PaymentMethod\w*|payment(?:[A-Z_\."'`]|$))\b/u,
  },
  {
    name: "project authentication/security surface",
    pattern: /\/api\/v\d+\/projects\/\{[^}]+\}\/(?:auth|security)(?:[\/"'`:]|$)|\b(?:SecurityClient|SecuritySettings\w*)\b|\bsecurity\.(?:\w+)/u,
  },
];

const files = [
  ...(await filesUnder(path.join(repositoryRoot, "src"), (filePath) => {
    const relative = path.relative(repositoryRoot, filePath);
    return filePath.endsWith(".ts")
      && !filePath.endsWith(".test.ts")
      && !relative.startsWith("src/integration/")
      && !relative.startsWith("src/better-auth.ts")
      && !filePath.endsWith("client-route-capture.ts");
  })),
  ...(await filesUnder(path.join(repositoryRoot, "dist"), (filePath) => {
    const relative = path.relative(repositoryRoot, filePath);
    return (filePath.endsWith(".js") || filePath.endsWith(".d.ts"))
      && !filePath.includes(".test.")
      && !relative.startsWith("dist/integration/")
      && !relative.startsWith("dist/internal/")
      && !relative.startsWith("dist/better-auth.")
      && !filePath.includes("capture");
  })),
  path.join(repositoryRoot, "README.md"),
  path.join(repositoryRoot, "sdk-route-manifest.json"),
];

const violations = [];
for (const filePath of files) {
  if (!(await isFile(filePath))) continue;
  const lines = (await readFile(filePath, "utf8")).split(/\r?\n/u);
  for (const [index, line] of lines.entries()) {
    for (const rule of forbidden) {
      if (rule.pattern.test(line)) {
        violations.push(`${path.relative(repositoryRoot, filePath)}:${index + 1} [${rule.name}] ${line.trim()}`);
      }
    }
  }
}

if (violations.length > 0) {
  console.error("Forbidden SDK surface detected:");
  for (const violation of violations) console.error(`  - ${violation}`);
  process.exitCode = 1;
} else {
  console.log("Forbidden SDK surface check passed.");
}

async function filesUnder(directory, predicate) {
  if (!(await isDirectory(directory))) return [];
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const filePath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...await filesUnder(filePath, predicate));
    } else if (entry.isFile() && predicate(filePath)) {
      files.push(filePath);
    }
  }
  return files;
}

async function isFile(filePath) {
  try {
    return (await stat(filePath)).isFile();
  } catch {
    return false;
  }
}

async function isDirectory(directory) {
  try {
    return (await stat(directory)).isDirectory();
  } catch {
    return false;
  }
}
