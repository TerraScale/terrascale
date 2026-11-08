import { DatabaseSync } from "node:sqlite";
import { describe, expect, it } from "vitest";
import schema from "../../schema.sql?raw";
import { isId, newId } from "../ids";
import type { Env } from "../env";
import { bootstrap } from "./bootstrap";

const tables = [
  "component_groups",
  "components",
  "endpoints",
  "incidents",
  "incident_updates",
  "subscribers",
  "outbox",
];

describe("clean D1 bootstrap", () => {
  it("drops the hosting provider from the TerraBase API labels", () => {
    const sqlite = new DatabaseSync(":memory:");
    try {
      sqlite.exec(schema);
      sqlite.exec(
        "UPDATE components SET description = 'api.terrascale.tech, served from OCI gru' WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1D2'; UPDATE regions SET ping_label = 'TerraBase API, OCI gru' WHERE code = 'GRU'",
      );
      sqlite.exec(schema);
      expect(
        sqlite.prepare("SELECT description FROM components WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1D2'").get()?.description,
      ).toBe("api.terrascale.tech, the global TerraBase endpoint");
      expect(sqlite.prepare("SELECT ping_label FROM regions WHERE code = 'GRU'").get()?.ping_label).toBe(
        "TerraBase API, gru",
      );
    } finally {
      sqlite.close();
    }
  });

  it("updates retired hosting metadata without rebuilding tables or losing history", () => {
    const sqlite = new DatabaseSync(":memory:");
    const endpoint = "01M4PVGGBHD58R8HYNDBTGFRGJ";
    try {
      const legacy = schema
        .split("\n")
        .filter((line) => !line.startsWith("UPDATE "))
        .join("\n")
        .replace("('cloudflare', 'other')", "('retired', 'cloudflare', 'other')")
        .replaceAll(", 'other',", ", 'retired',");
      sqlite.exec(legacy);
      sqlite
        .prepare(
          "UPDATE endpoints SET url = 'https://ts-terrabase.retired.dev/health/replication', headers = ?, live_since = '2026-10-11 00:00:00' WHERE id = ?",
        )
        .run(
          JSON.stringify({ "RETIRED-Prefer-Region": "gru", "x-prefer-region": "custom", "Cache-Control": "no-cache" }),
          endpoint,
        );
      sqlite
        .prepare(
          "INSERT INTO probe_samples (endpoint_id, at, state, ok, total, results) VALUES (?, '2026-10-11 00:00:00', 'operational', 1, 1, '[]')",
        )
        .run(endpoint);
      sqlite.exec(
        "UPDATE components SET description = 'api.terrascale.tech, served from every Retired.io region' WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1D2'; UPDATE regions SET ping_label = 'TerraBase API, nearest Retired.io region' WHERE code = 'GRU'; UPDATE regions SET ping_label = 'Custom probe' WHERE code = 'SJC'; UPDATE endpoints SET url = 'https://operator.test/health', headers = '{\"x-prefer-region\":\"custom\"}' WHERE id = '01M4PVGGBJ24A140MWGJV7MVKV'",
      );
      const assertions = sqlite.prepare("SELECT assertions FROM endpoints WHERE id = ?").get(endpoint)?.assertions;
      const ids = sqlite.prepare("SELECT id FROM endpoints ORDER BY id").all();
      sqlite.exec(schema);
      expect(
        sqlite
          .prepare("SELECT provider, url, headers, live_since, assertions FROM endpoints WHERE id = ?")
          .get(endpoint),
      ).toEqual({
        provider: "other",
        url: "https://api.terrascale.tech/health/replication",
        headers: '{"x-prefer-region":"custom","Cache-Control":"no-cache"}',
        live_since: "2026-10-11 00:00:00",
        assertions,
      });
      expect(
        sqlite.prepare("SELECT description FROM components WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1D2'").get()?.description,
      ).toBe("api.terrascale.tech, the global TerraBase endpoint");
      expect(
        sqlite.prepare("SELECT code, ping_label FROM regions WHERE code IN ('GRU', 'SJC') ORDER BY code").all(),
      ).toEqual([
        { code: "GRU", ping_label: "TerraBase API, gru" },
        { code: "SJC", ping_label: "Custom probe" },
      ]);
      expect(
        sqlite.prepare("SELECT provider, url, headers FROM endpoints WHERE id = '01M4PVGGBJ24A140MWGJV7MVKV'").get(),
      ).toEqual({
        provider: "other",
        url: "https://operator.test/health",
        headers: '{"x-prefer-region":"custom"}',
      });
      expect(sqlite.prepare("SELECT count(*) AS n FROM probe_samples").get()?.n).toBe(1);
      expect(sqlite.prepare("SELECT id FROM endpoints ORDER BY id").all()).toEqual(ids);
      sqlite
        .prepare("UPDATE endpoints SET url = 'https://operator.test/health', headers = '{}' WHERE id = ?")
        .run(endpoint);
      sqlite.exec(schema);
      expect(sqlite.prepare("SELECT url FROM endpoints WHERE id = ?").get(endpoint)?.url).toBe(
        "https://operator.test/health",
      );
      expect(sqlite.prepare("PRAGMA foreign_key_check").all()).toEqual([]);
    } finally {
      sqlite.close();
    }
  });

  it("upgrades old replication assertions without losing launch state, history or operator checks", () => {
    const sqlite = new DatabaseSync(":memory:");
    const endpoint = "01M4PVGGBHD58R8HYNDBTGFRGJ";
    const old = '[{"path":"status","op":"eq","value":"ok"},{"path":"region","op":"eq","value":"gru"}]';
    try {
      sqlite.exec(schema);
      sqlite
        .prepare("UPDATE endpoints SET assertions = ?, live_since = '2026-10-11 00:00:00' WHERE id = ?")
        .run(old, endpoint);
      sqlite.exec(`
        INSERT INTO probe_samples (endpoint_id, at, state, ok, total, results)
          VALUES ('${endpoint}', '2026-10-11 00:00:00', 'operational', 4, 4, '[]');
        UPDATE endpoints SET assertions = '[{"path":"operator","op":"eq","value":true}]'
          WHERE id = '01M4PVGGBJ24A140MWGJV7MVKV';
      `);
      sqlite.exec(schema);
      sqlite.exec(schema);
      const row = sqlite.prepare("SELECT assertions, live_since FROM endpoints WHERE id = ?").get(endpoint)!;
      expect(JSON.parse(String(row.assertions))).toEqual([{ path: "status", op: "eq", value: "ok" }]);
      expect(sqlite.prepare("SELECT region_code, cluster, headers FROM endpoints WHERE id = ?").get(endpoint)).toEqual({
        region_code: null,
        cluster: "reported peers",
        headers: "{}",
      });
      expect(row.live_since).toBe("2026-10-11 00:00:00");
      expect(sqlite.prepare("SELECT count(*) AS n FROM probe_samples").get()?.n).toBe(1);
      expect(
        sqlite.prepare("SELECT assertions FROM endpoints WHERE id = '01M4PVGGBJ24A140MWGJV7MVKV'").get()?.assertions,
      ).toBe('[{"path":"operator","op":"eq","value":true}]');
    } finally {
      sqlite.close();
    }
  });

  it("upgrades a store seeded before the dashboard probe and single auth readiness", () => {
    const sqlite = new DatabaseSync(":memory:");
    const pinned = '[{"path":"status","op":"eq","value":"ready"},{"path":"region","op":"eq","value":"gru"}]';
    // TerraBase is SQL-only: the anonymous read check uses the database route.
    const database = "https://api.terrascale.tech/v1/projects/01M4E7G1W82VKFPF5FJ9AVK1DT/databases/status_canary";
    try {
      sqlite.exec(schema);
      // The earlier seed: a deferred dashboard, a region-pinned auth check and its SJC/FRA copies.
      sqlite.exec(`
        DELETE FROM endpoints WHERE component_id = '01M4E7G1W82VKFPF5FJ9AVK1CV';
        UPDATE components SET lifecycle = 'planned' WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1CV';
        UPDATE endpoints SET assertions = '${pinned}' WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1D6';
        UPDATE endpoints SET url = 'https://operator.test/read' WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1E0';
        UPDATE regions SET ping_url = 'https://api.terrascale.tech/healthz', ping_label = 'TerraBase API, OCI gru' WHERE code IN ('SJC', 'FRA');
        INSERT INTO endpoints (id, component_id, region_code, cluster, provider, url) VALUES
          ('01M4E7G1W82VKFPF5FJ9AVK1DX', '01M4E7G1W82VKFPF5FJ9AVK1CT', 'SJC', 'sjc', 'other', 'https://auth.terrascale.tech/readyz'),
          ('01M4E7G1W82VKFPF5FJ9AVK1DZ', '01M4E7G1W82VKFPF5FJ9AVK1CT', 'FRA', 'fra', 'other', 'https://operator.test/readyz');
        INSERT INTO probe_samples (endpoint_id, at, state, ok, total, results) VALUES
          ('01M4E7G1W82VKFPF5FJ9AVK1DX', '2026-10-11 00:00:00', 'major_outage', 0, 4, '[]');
      `);
      sqlite.exec(schema);
      const one = (query: string) => sqlite.prepare(query).get();
      expect(one("SELECT lifecycle FROM components WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1CV'")?.lifecycle).toBe("active");
      expect(one("SELECT url FROM endpoints WHERE component_id = '01M4E7G1W82VKFPF5FJ9AVK1CV'")?.url).toBe(
        "https://dash.terrascale.tech/health/ready",
      );
      expect(one("SELECT assertions FROM endpoints WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1D6'")?.assertions).toBe(
        '[{"path":"status","op":"eq","value":"ready"}]',
      );
      // Seeded values move on; an operator's own URL or endpoint stays.
      expect(
        sqlite
          .prepare(
            "SELECT id, url FROM endpoints WHERE id IN ('01M4E7G1W82VKFPF5FJ9AVK1DB', '01M4E7G1W82VKFPF5FJ9AVK1DC', '01M4E7G1W82VKFPF5FJ9AVK1E0', '01M4E7G1W82VKFPF5FJ9AVK1DX', '01M4E7G1W82VKFPF5FJ9AVK1DZ') ORDER BY id",
          )
          .all(),
      ).toEqual([
        { id: "01M4E7G1W82VKFPF5FJ9AVK1DB", url: database },
        { id: "01M4E7G1W82VKFPF5FJ9AVK1DC", url: database },
        { id: "01M4E7G1W82VKFPF5FJ9AVK1DZ", url: "https://operator.test/readyz" },
        { id: "01M4E7G1W82VKFPF5FJ9AVK1E0", url: "https://operator.test/read" },
      ]);
      expect(one("SELECT count(*) AS n FROM probe_samples")?.n).toBe(0);
      expect(sqlite.prepare("SELECT code FROM regions WHERE ping_url IS NOT NULL").all()).toEqual([{ code: "GRU" }]);
      // An operator deferring the dashboard again is respected.
      sqlite.exec("UPDATE components SET lifecycle = 'planned' WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1CV'");
      sqlite.exec(schema);
      expect(one("SELECT lifecycle FROM components WHERE id = '01M4E7G1W82VKFPF5FJ9AVK1CV'")?.lifecycle).toBe(
        "planned",
      );
      expect(sqlite.prepare("PRAGMA foreign_key_check").all()).toEqual([]);
    } finally {
      sqlite.close();
    }
  });

  it("recreates an empty store on startup and preserves IDs and operator edits on repeat", async () => {
    const sqlite = new DatabaseSync(":memory:");
    let executions = 0;
    const env = {
      STATUS_DB: {
        exec: async (sql: string) => {
          executions++;
          for (const statement of sql.trim().split("\n")) sqlite.exec(statement);
        },
      },
    } as unknown as Env;
    try {
      await Promise.all([bootstrap(env), bootstrap(env)]);
      expect(executions).toBe(1);
      const ids = sqlite.prepare("SELECT id FROM endpoints ORDER BY id").all();
      expect(ids).toHaveLength(21);
      expect(ids.every((row) => isId(row.id))).toBe(true);
      const incidentId = newId();
      sqlite
        .prepare("INSERT INTO incidents (id, title, status, impact) VALUES (?, 'Outage', 'investigating', 'minor')")
        .run(incidentId);
      sqlite.exec(
        "UPDATE endpoints SET live_since = '2026-10-11 00:00:00'; UPDATE components SET lifecycle = 'planned'",
      );
      sqlite.exec(schema);
      expect(sqlite.prepare("SELECT id FROM endpoints ORDER BY id").all()).toEqual(ids);
      expect(sqlite.prepare("SELECT id FROM incidents").get()?.id).toBe(incidentId);
      expect(sqlite.prepare("SELECT count(*) AS n FROM endpoints WHERE live_since IS NULL").get()?.n).toBe(0);
      expect(sqlite.prepare("SELECT count(*) AS n FROM components WHERE lifecycle = 'active'").get()?.n).toBe(0);
      expect(sqlite.prepare("PRAGMA foreign_key_check").all()).toEqual([]);
    } finally {
      sqlite.close();
    }
  });

  it("enforces canonical, non-null primary IDs in every entity table", () => {
    const sqlite = new DatabaseSync(":memory:");
    try {
      sqlite.exec(schema);
      const incident = newId(),
        update = newId(),
        subscriber = newId();
      sqlite
        .prepare("INSERT INTO incidents (id, title, status, impact) VALUES (?, 'Outage', 'investigating', 'minor')")
        .run(incident);
      sqlite
        .prepare(
          "INSERT INTO incident_updates (id, incident_id, status, body) VALUES (?, ?, 'investigating', 'Looking.')",
        )
        .run(update, incident);
      sqlite
        .prepare("INSERT INTO subscribers (id, channel, target) VALUES (?, 'email', 'test@example.com')")
        .run(subscriber);
      sqlite
        .prepare("INSERT INTO outbox (id, subscriber_id, kind, update_id) VALUES (?, ?, 'update', ?)")
        .run(newId(), subscriber, update);
      for (const table of tables) {
        const row = sqlite.prepare("SELECT id FROM " + table + " LIMIT 1").get();
        expect(row).toBeDefined();
        for (const id of [null, "legacy", "01m4e7g1w82vkfpf5fj9avk1cv", "8ZZZZZZZZZZZZZZZZZZZZZZZZZ"])
          expect(() => sqlite.prepare("UPDATE " + table + " SET id = ? WHERE id = ?").run(id, row!.id!)).toThrow();
      }
      expect(() =>
        sqlite.exec(
          "INSERT INTO incidents (id, title, status, impact) VALUES ('uuid', 'Outage', 'investigating', 'minor')",
        ),
      ).toThrow(/CHECK/);
    } finally {
      sqlite.close();
    }
  });
});
