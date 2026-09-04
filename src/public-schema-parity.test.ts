import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "vitest";

import type {
  AccessRole,
  CreateDatabaseBranchRequest,
  DatabaseBranchCapabilityResponse,
  DeleteProjectDatabaseOptions,
  ProjectSavedQueryPageRequest,
  ProjectSavedQueryPageResponse,
  ProjectSavedQueryResponse,
  TeamMember,
  Timestamp,
  UpdateProjectSavedQueryRequest,
} from "./models.js";
import type {
  BackupsClient,
  DatabaseBranchesClient,
  DatabasesClient,
  SavedQueriesClient,
} from "./client.js";
import type { TerraScaleClient } from "./factory.js";
import type { TerraScaleResult } from "./result.js";

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

type ExpectedSavedQueryResponse = {
  readonly savedQueryId: string;
  readonly projectId: string;
  readonly name: string;
  readonly query: string;
  readonly createdAt: Timestamp;
  readonly updatedAt: Timestamp;
  readonly revision: string;
};

type ExpectedSavedQueryPage = {
  readonly items: readonly ProjectSavedQueryResponse[];
  readonly totalCount: number;
  readonly nextCursor: string | null;
};

type ExpectedDatabaseBranchCapability = {
  readonly projectId: string;
  readonly admissionState: string;
  readonly physicalCloneAvailable: boolean;
  readonly promotionAvailable: boolean;
  readonly mergeAvailable: boolean;
  readonly detail: string;
};

type ExpectedProjectTeamMember = {
  readonly userId: string;
  readonly email: string;
  readonly displayName: string;
  readonly role: AccessRole;
  readonly joinedAt: Timestamp | null;
};

type ExpectedCreateDatabaseBranchRequest = {
  readonly sourceDatabaseId?: string;
};

const staticSdkParity: readonly [
  Assert<
    Equal<
      ProjectSavedQueryPageRequest,
      { readonly cursor?: string; readonly skip?: number; readonly take?: number }
    >
  >,
  Assert<Equal<ProjectSavedQueryPageResponse, ExpectedSavedQueryPage>>,
  Assert<Equal<ProjectSavedQueryResponse, ExpectedSavedQueryResponse>>,
  Assert<Equal<Pick<UpdateProjectSavedQueryRequest, "revision">, { readonly revision: string }>>,
  Assert<Equal<DatabaseBranchCapabilityResponse, ExpectedDatabaseBranchCapability>>,
  Assert<Equal<CreateDatabaseBranchRequest, ExpectedCreateDatabaseBranchRequest>>,
  Assert<
    Equal<
      DatabaseBranchesClient["requestClone"],
      (
        request: ExpectedCreateDatabaseBranchRequest,
        projectId?: string,
      ) => Promise<TerraScaleResult<never>>
    >
  >,
  Assert<Equal<TeamMember, ExpectedProjectTeamMember>>,
  Assert<MissingKey<TerraScaleClient, "request">>,
  Assert<MissingKey<PublicSdk, "TerraScaleHttp">>,
  Assert<Equal<Parameters<SavedQueriesClient["update"]>[1], UpdateProjectSavedQueryRequest>>,
  Assert<MissingKey<BackupsClient, "triggerBackup">>,
  Assert<Equal<Parameters<DatabasesClient["delete"]>["length"], 2>>,
  Assert<Equal<Parameters<DatabasesClient["delete"]>[1], DeleteProjectDatabaseOptions>>,
] = [
  true,
  true,
  true,
  true,
  true,
  true,
  true,
  true,
  true,
  true,
  true,
  true,
  true,
  true,
];
void staticSdkParity;

describe("public schema parity", () => {
  test("keeps saved-query revision and page routes in the generated descriptor", async () => {
    const descriptor = await readDescriptor();
    const list = descriptor.routes.find(
      ({ operationId }) => operationId === "ListProjectSavedQueries",
    );
    const update = descriptor.routes.find(
      ({ operationId }) => operationId === "UpdateProjectSavedQuery",
    );

    assert.equal(list?.method, "GET");
    assert.equal(list?.pathTemplate, "/api/v1/projects/{projectId}/saved-queries");
    assert.equal(update?.method, "PUT");
    assert.equal(update?.pathTemplate, "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}");
  });

  test("keeps physical database branching routes pinned", async () => {
    const descriptor = await readDescriptor();
    const capability = descriptor.routes.find(
      ({ operationId }) => operationId === "GetDatabaseBranchCapability",
    );
    const clone = descriptor.routes.find(
      ({ operationId }) => operationId === "CreateDatabaseBranch",
    );

    assert.equal(capability?.method, "GET");
    assert.equal(
      capability?.pathTemplate,
      "/api/v1/projects/{projectId}/database-branches/capability",
    );
    assert.equal(clone?.method, "POST");
    assert.equal(clone?.pathTemplate, "/api/v1/projects/{projectId}/database-branches");
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
