import type { Judgement, Observation } from "./assertions";

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const regionName = (value: unknown): string | null =>
  typeof value === "string" && value.trim() !== "" ? value.trim().toLowerCase() : null;

const validLag = (value: unknown): value is number =>
  typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 30;

const peerError = (peer: Record<string, unknown>): string | null => {
  if (peer.status !== "ok") return `Peer ${peer.region}: expected status ok`;
  if (typeof peer.lagSeconds !== "number" || !Number.isFinite(peer.lagSeconds) || peer.lagSeconds < 0)
    return `Peer ${peer.region}: missing or invalid lagSeconds`;
  if (peer.lagSeconds > 30) return `Peer ${peer.region}: lagSeconds exceeds 30 seconds`;
  return null;
};

/** Judges the observer's whole topology, or one peer reported by that observer. */
export function judgeReplication(observation: Observation, region?: string | null): Judgement {
  const fail = (error: string): Judgement => ({ outcome: "fail", error, values: {} });
  const unavailable = (error: string): Judgement => ({
    outcome: region == null ? "fail" : "unknown",
    error,
    values: {},
  });
  if (observation.status === null) return unavailable(observation.error ?? "Unreachable");
  if (observation.status !== 200) return unavailable(`HTTP ${observation.status}`);

  let report: unknown;
  try {
    report = JSON.parse(observation.body ?? "");
  } catch {
    return unavailable("Invalid JSON");
  }
  if (!isObject(report)) return fail("Invalid replication report");
  const observer = regionName(report.region);
  if (observer === null) return fail("Invalid report region");
  if (!Array.isArray(report.peers)) return fail("Expected peers array");

  const peers: Record<string, unknown>[] = [];
  const seen = new Set<string>();
  for (const [index, peer] of report.peers.entries()) {
    if (!isObject(peer)) return fail(`Invalid peer at index ${index}`);
    const name = regionName(peer.region);
    if (name === null) return fail(`Invalid peer region at index ${index}`);
    if (name === observer) return fail(`Peer ${peer.region}: cannot report observer as a peer`);
    if (seen.has(name)) return fail(`Duplicate peer ${peer.region}`);
    seen.add(name);
    peers.push(peer);
  }

  const scope = region == null ? null : regionName(region);
  if (region != null && scope !== observer) {
    const peer = peers.find((entry) => regionName(entry.region) === scope);
    if (!peer) return { outcome: "unknown", error: "Not reported", values: {} };
    const error = peerError(peer);
    return error ? fail(error) : { outcome: "ok", error: null, values: { lag: peer.lagSeconds } };
  }

  let maxPeerLag = 0;
  for (const peer of peers) {
    const error = peerError(peer);
    if (error) return fail(error);
    maxPeerLag = Math.max(maxPeerLag, peer.lagSeconds as number);
  }
  if (report.status !== "ok") return fail("Expected report status ok");
  if (peers.length === 0) {
    if (report.maxLagSeconds !== null && report.maxLagSeconds !== 0) {
      return fail("Expected maxLagSeconds null or 0 for empty peers");
    }
  } else {
    if (!validLag(report.maxLagSeconds)) return fail("Expected finite maxLagSeconds between 0 and 30");
    if (report.maxLagSeconds < maxPeerLag) return fail("maxLagSeconds is below the maximum reported peer lag");
  }
  return { outcome: "ok", error: null, values: { lag: report.maxLagSeconds } };
}
