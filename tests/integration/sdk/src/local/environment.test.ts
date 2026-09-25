import { describe, expect, test } from "vitest";

import { assertLocalSqliteEnvironment, localSqliteHostedOptIn } from "./environment.js";

const developmentEnvironment = {
  NODE_ENV: "development",
  TERRASCALE_DEPLOYMENT_ENVIRONMENT: "development",
} as const;

describe("hosted SQLite development environments", () => {
  test("refuses a hosting platform indicator without the explicit opt-in", () => {
    expect(() =>
      assertLocalSqliteEnvironment({ ...developmentEnvironment, FLY_APP_NAME: "ts-dashboard-dev" }),
    ).toThrow(/FLY_APP_NAME/);
  });

  test("allows a hosting platform indicator with the explicit opt-in", () => {
    expect(() =>
      assertLocalSqliteEnvironment({
        ...developmentEnvironment,
        FLY_APP_NAME: "ts-dashboard-dev",
        [localSqliteHostedOptIn]: "1",
      }),
    ).not.toThrow();
  });

  for (const [name, value] of [
    ["CI", "true"],
    ["GITHUB_ACTIONS", "true"],
    ["TERRASCALE_QUALIFICATION", "true"],
  ] as const) {
    test(`still refuses ${name} with the explicit opt-in`, () => {
      expect(() =>
        assertLocalSqliteEnvironment({
          ...developmentEnvironment,
          FLY_APP_NAME: "ts-dashboard-dev",
          [localSqliteHostedOptIn]: "1",
          [name]: value,
        }),
      ).toThrow(new RegExp(name));
    });
  }

  for (const [name, value] of [
    ["NODE_ENV", "production"],
    ["DEPLOYMENT_ENVIRONMENT", "staging"],
    ["STAGE", "preview"],
  ] as const) {
    test(`still refuses ${name}=${value} with the explicit opt-in`, () => {
      expect(() =>
        assertLocalSqliteEnvironment({
          FLY_APP_NAME: "ts-dashboard-dev",
          [localSqliteHostedOptIn]: "1",
          [name]: value,
        }),
      ).toThrow(/disabled for/);
    });
  }
});
