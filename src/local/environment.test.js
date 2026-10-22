import { describe, expect, test } from "vitest";

import { assertLocalSqliteEnvironment, localSqliteHostedOptIn } from "./environment.js";

const developmentEnvironment = /** @type {const} */ ({
  NODE_ENV: "development",
  TERRASCALE_DEPLOYMENT_ENVIRONMENT: "development",
});

describe("hosted SQLite development environments", () => {
  test("refuses a hosting platform indicator without the explicit opt-in", () => {
    expect(() =>
      assertLocalSqliteEnvironment({ ...developmentEnvironment, RENDER: "ts-dashboard-dev" }),
    ).toThrow(/RENDER/);
  });

  test("allows a hosting platform indicator with the explicit opt-in", () => {
    expect(() =>
      assertLocalSqliteEnvironment({
        ...developmentEnvironment,
        RENDER: "ts-dashboard-dev",
        [localSqliteHostedOptIn]: "1",
      }),
    ).not.toThrow();
  });

  for (const [name, value] of /** @type {const} */ ([
    ["CI", "true"],
    ["GITHUB_ACTIONS", "true"],
    ["TERRASCALE_QUALIFICATION", "true"],
  ])) {
    test(`still refuses ${name} with the explicit opt-in`, () => {
      expect(() =>
        assertLocalSqliteEnvironment({
          ...developmentEnvironment,
          RENDER: "ts-dashboard-dev",
          [localSqliteHostedOptIn]: "1",
          [name]: value,
        }),
      ).toThrow(new RegExp(name));
    });
  }

  for (const [name, value] of /** @type {const} */ ([
    ["NODE_ENV", "production"],
    ["DEPLOYMENT_ENVIRONMENT", "staging"],
    ["STAGE", "preview"],
  ])) {
    test(`still refuses ${name}=${value} with the explicit opt-in`, () => {
      expect(() =>
        assertLocalSqliteEnvironment({
          RENDER: "ts-dashboard-dev",
          [localSqliteHostedOptIn]: "1",
          [name]: value,
        }),
      ).toThrow(/disabled for/);
    });
  }
});
