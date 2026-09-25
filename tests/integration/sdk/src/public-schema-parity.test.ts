import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "vitest";

import type { DeleteProjectDatabaseOptions } from "./models.js";
import type {
  BackupsClient,
  DatabasesClient,
} from "./client.js";
import type { TerraScaleClient } from "./factory.js";

type Equal<Left, Right> =
  (<Value>() => Value extends Left ? 1 : 2) extends <Value>() => Value extends Right ? 1 : 2
    ? (<Value>() => Value extends Right ? 1 : 2) extends <Value>() => Value extends Left ? 1 : 2
      ? true
      : false
    : false;
type Assert<Condition extends true> = Condition;
type MissingKey<Container, Key extends PropertyKey> =
  Extract<keyof Container, Key> extends never ? true : false;
type PublicSdk = typeof import("./index.js");

const staticSdkParity: readonly [
  Assert<MissingKey<TerraScaleClient, "request">>,
  Assert<MissingKey<PublicSdk, "TerraScaleHttp">>,
  Assert<MissingKey<BackupsClient, "triggerBackup">>,
  Assert<Equal<Parameters<DatabasesClient["delete"]>["length"], 2>>,
  Assert<Equal<Parameters<DatabasesClient["delete"]>[1], DeleteProjectDatabaseOptions>>,
] = [
  true,
  true,
  true,
  true,
  true,
];
void staticSdkParity;

describe("public schema parity", () => {
  test("keeps canonical database branching routes pinned", async () => {
    const descriptor = await readDescriptor();
    const list = descriptor.routes.find(
      ({ operationId }) => operationId === "list_branches",
    );
    const clone = descriptor.routes.find(
      ({ operationId }) => operationId === "create_branch",
    );

    assert.equal(list?.method, "GET");
    assert.equal(list?.pathTemplate, "/api/v1/databases/{name}/branches");
    assert.equal(clone?.method, "POST");
    assert.equal(clone?.pathTemplate, "/api/v1/databases/{name}/branches");
  });

});

async function readDescriptor(): Promise<{
  readonly routes: readonly {
    readonly client: string;
    readonly operationId: string;
    readonly method: string;
    readonly pathTemplate: string;
    readonly routeName: string;
  }[];
}> {
  return JSON.parse(
    await readFile(new URL("../sdk-openapi-contract-descriptor.json", import.meta.url), "utf8"),
  ) as {
    readonly routes: readonly {
      readonly client: string;
      readonly operationId: string;
      readonly method: string;
      readonly pathTemplate: string;
      readonly routeName: string;
    }[];
  };
}
