import type { Env } from "../env";
import type { TargetResult } from "./protocol";
import { VANTAGES, vantageStub } from "./vantages";

const SERVICES = [
  ["ts-terrabase", "https://api.terrascale.tech/healthz"],
  ["ts-auth", "https://auth.terrascale.tech/healthz"],
  ["ts-status", "https://status.terrascale.tech/health/version"],
  ["ts-dashboard", "https://dash.terrascale.tech/health/ready"],
] as const;

export interface DeploymentObservation {
  service: string;
  region: string;
  target: "service" | "vantage";
  version: string | null;
  status: number | null;
  colo: string | null;
  observedAt: string;
}

/** The commit: the whole version, or the `+<revision>` build metadata ts-auth reports. */
const revision = (value: unknown): string | null => {
  const commit = typeof value === "string" ? value.split("+").pop() : undefined;
  return commit !== undefined && /^[0-9a-f]{40}$/.test(commit) ? commit : null;
};

interface DeploymentProbe {
  version?: string;
  colo?: string | null;
  results: Pick<TargetResult, "key" | "status" | "body">[];
}

function isDeploymentProbe(value: unknown): value is DeploymentProbe {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const probe = value as Record<string, unknown>;
  return (
    (probe.version === undefined || typeof probe.version === "string") &&
    (probe.colo === undefined || probe.colo === null || typeof probe.colo === "string") &&
    Array.isArray(probe.results) &&
    probe.results.every((value: unknown) => {
      if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
      const result = value as Record<string, unknown>;
      return (
        typeof result.key === "string" &&
        (result.status === null ||
          (typeof result.status === "number" &&
            Number.isInteger(result.status) &&
            result.status >= 100 &&
            result.status <= 599)) &&
        (result.body === undefined || typeof result.body === "string")
      );
    })
  );
}

/** Fixed public targets only: callers cannot use the vantages as a proxy. */
export async function deployments(env: Env, now: number): Promise<DeploymentObservation[]> {
  const observedAt = new Date(now).toISOString();
  const targets = SERVICES.map(([key, url]) => ({
    key,
    url,
    headers: JSON.stringify({
      "Cache-Control": "no-cache",
      ...(key === "ts-terrabase" ? { "X-TerraScale-Region-Pin": "gru" } : {}),
    }),
    body: true,
  }));
  const regions = VANTAGES.filter((vantage) => vantage.code === "gru" || vantage.code === "sfo");
  return (
    await Promise.all(
      regions.map(async (vantage) => {
        const region = vantage.code.toUpperCase();
        let probe: DeploymentProbe | null = null;
        try {
          const response = await vantageStub(env, vantage).fetch(`https://vantage/${vantage.code}`, {
            method: "POST",
            body: JSON.stringify({ targets }),
            signal: AbortSignal.timeout(8_000),
          });
          if (response.ok) {
            const body: unknown = await response.json();
            if (isDeploymentProbe(body)) probe = body;
          }
        } catch {
          // A missing vantage is a missing deployment proof, never a healthy sample.
        }
        const observations: DeploymentObservation[] = SERVICES.map(([service]) => {
          const result = probe?.results?.find((item) => item.key === service);
          let version: string | null = null;
          try {
            if (result?.status === 200) version = revision(JSON.parse(result.body ?? "").version);
          } catch {
            /* Invalid bodies remain unknown. */
          }
          return {
            service,
            region,
            target: "service",
            version,
            status: result?.status ?? null,
            colo: probe?.colo ?? null,
            observedAt,
          };
        });
        observations.push({
          service: "ts-status",
          region,
          target: "vantage",
          version: revision(probe?.version),
          status: probe ? 200 : null,
          colo: probe?.colo ?? null,
          observedAt,
        });
        return observations;
      }),
    )
  ).flat();
}
