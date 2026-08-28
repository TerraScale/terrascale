import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import type {
  AccessRole,
  CreateDatabaseBranchRequest,
  DatabaseBranchCapabilityResponse,
  DeleteProjectDatabaseOptions,
  ProjectSavedQueryResponse,
  ProjectSavedQuerySnapshotPageRequest,
  ProjectSavedQuerySnapshotPageResponse,
  ProjectSavedQueryV2Response,
  TeamMember,
  Timestamp,
  UpdateProjectSavedQueryV2Request,
} from "./models.js";
import type {
  BackupsClient,
  DatabaseBranchesClient,
  DatabasesClient,
  DocumentsClient,
  QueryClient,
  SavedQueriesClient,
  SavedQueriesV3Client,
} from "./client.js";
import type { TerraScaleClient } from "./factory.js";
import type { Ulid } from "./ids.js";
import type { TerraScaleResult } from "./result.js";

type Equal<Left, Right> =
  (<Value>() => Value extends Left ? 1 : 2) extends (<Value>() => Value extends Right ? 1 : 2)
    ? (<Value>() => Value extends Right ? 1 : 2) extends (<Value>() => Value extends Left ? 1 : 2)
      ? true
      : false
    : false;
type Assert<Condition extends true> = Condition;
type MissingKey<Container, Key extends PropertyKey> = Extract<keyof Container, Key> extends never
  ? true
  : false;
type PublicSdk = typeof import("./index.js");

type ExpectedSnapshotRequest = {
  readonly cursor?: string;
  readonly take?: number;
};

type ExpectedSavedQueryResponse = {
  readonly savedQueryId: Ulid;
  readonly projectId: Ulid;
  readonly name: string;
  readonly query: string;
  readonly repositoryId: Ulid | null;
  readonly createdAt: Timestamp;
  readonly updatedAt: Timestamp;
};

type ExpectedSnapshotPage = {
  readonly items: readonly ProjectSavedQueryV2Response[];
  readonly nextCursor?: string | null;
};

type ExpectedDatabaseBranchCapability = {
  readonly projectId: Ulid;
  readonly admissionState: string;
  readonly physicalCloneAvailable: boolean;
  readonly promotionAvailable: boolean;
  readonly mergeAvailable: boolean;
  readonly detail: string;
};

type ExpectedProjectTeamMember = {
  readonly userId: Ulid;
  readonly email: string;
  readonly displayName: string;
  readonly role: AccessRole;
  readonly joinedAt: Timestamp | null;
};

type ExpectedCreateDatabaseBranchRequest = {
  readonly sourceDatabaseId?: Ulid;
};

type ExpectedSnapshotList = (
  projectId?: Ulid,
  query?: ExpectedSnapshotRequest,
) => Promise<TerraScaleResult<ExpectedSnapshotPage>>;

const staticSdkParity: readonly [
  Assert<Equal<ProjectSavedQuerySnapshotPageRequest, ExpectedSnapshotRequest>>,
  Assert<Equal<ProjectSavedQuerySnapshotPageResponse, ExpectedSnapshotPage>>,
  Assert<Equal<SavedQueriesV3Client["list"], ExpectedSnapshotList>>,
  Assert<Equal<SavedQueriesClient["v3"], SavedQueriesV3Client>>,
  Assert<Equal<DatabaseBranchCapabilityResponse, ExpectedDatabaseBranchCapability>>,
  Assert<Equal<CreateDatabaseBranchRequest, ExpectedCreateDatabaseBranchRequest>>,
  Assert<Equal<DatabaseBranchesClient["requestClone"], (request: ExpectedCreateDatabaseBranchRequest, projectId?: Ulid) => Promise<TerraScaleResult<never>>>>,
  Assert<Equal<TeamMember, ExpectedProjectTeamMember>>,
  Assert<MissingKey<TerraScaleClient, "request">>,
  Assert<MissingKey<PublicSdk, "TerraScaleHttp">>,
  Assert<Equal<Parameters<DocumentsClient["list"]>["length"], 1 | 2>>,
  Assert<Equal<Parameters<QueryClient["execute"]>["length"], 2>>,
  Assert<Equal<ProjectSavedQueryResponse, ExpectedSavedQueryResponse>>,
  Assert<Equal<Pick<UpdateProjectSavedQueryV2Request, "revision">, { readonly revision: string }>>,
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
  true,
  true,
  true,
];
void staticSdkParity;

type PublicContractFixture = {
  readonly savedQueriesV3: {
    readonly method: string;
    readonly operationId: string;
    readonly path: string;
    readonly parameters: readonly {
      readonly name: string;
      readonly required: boolean;
      readonly types: readonly string[];
      readonly maxLength?: number;
      readonly minimum?: number;
      readonly maximum?: number;
      readonly default?: number;
    }[];
    readonly response: {
      readonly required: readonly string[];
      readonly itemsRef: string;
      readonly nextCursorTypes: readonly string[];
    };
    readonly errorStatuses: readonly number[];
  };
  readonly databaseBranches: {
    readonly capability: {
      readonly path: string;
      readonly method: string;
      readonly operationId: string;
      readonly required: readonly string[];
      readonly propertyTypes: Readonly<Record<string, readonly (string | null)[]>>;
      readonly errorStatuses: readonly number[];
    };
    readonly clone: {
      readonly path: string;
      readonly method: string;
      readonly operationId: string;
      readonly sourceDatabaseIdRequired: boolean;
      readonly hasSuccessResponse: boolean;
      readonly errorStatuses: readonly number[];
    };
  };
};

describe("public contract fixture parity", () => {
  test("keeps the v3 snapshot route, cursor constraints, page, and error statuses pinned", async () => {
    const contract = await readContract();
    const v3 = contract.savedQueriesV3;

    assert.equal(v3.method, "get");
    assert.equal(v3.operationId, "ListProjectSavedQueriesV3");
    assert.equal(v3.path, "/api/v3/projects/{projectId}/saved-queries");
    assert.deepEqual(v3.parameters, [
      { name: "cursor", required: false, types: ["string"], maxLength: 4096 },
      { name: "take", required: false, types: ["integer"], minimum: 1, maximum: 500, default: 100 },
    ]);
    assert.deepEqual(v3.response, {
      required: ["items"],
      itemsRef: "#/components/schemas/ProjectSavedQueryV2Response",
      nextCursorTypes: ["string", "null"],
    });
    assert.deepEqual(v3.errorStatuses, [400, 401, 403, 404, 409, 503]);
  });

  test("keeps physical database branching fail-closed and clone without a success response", async () => {
    const branches = (await readContract()).databaseBranches;

    assert.deepEqual(branches.capability, {
      path: "/api/v1/projects/{projectId}/database-branches/capability",
      method: "get",
      operationId: "GetDatabaseBranchCapability",
      required: ["projectId", "admissionState", "physicalCloneAvailable", "promotionAvailable", "mergeAvailable", "detail"],
      propertyTypes: {
        projectId: ["string"], admissionState: ["string"], physicalCloneAvailable: ["boolean"],
        promotionAvailable: ["boolean"], mergeAvailable: ["boolean"], detail: ["string"],
      },
      errorStatuses: [400, 401, 403, 404, 409, 429, 500, 503],
    });
    assert.deepEqual(branches.clone, {
      path: "/api/v1/projects/{projectId}/database-branches",
      method: "post",
      operationId: "CreateDatabaseBranch",
      sourceDatabaseIdRequired: false,
      hasSuccessResponse: false,
      errorStatuses: [400, 401, 403, 404, 409, 429, 500, 503],
    });
  });

  test("keeps project team member display name and nullable join time required", async () => {
    const descriptor = JSON.parse(
      await readFile(new URL("../sdk-openapi-contract-descriptor.json", import.meta.url), "utf8"),
    ) as {
      readonly routes: readonly {
        readonly operationId: string;
        readonly responses: readonly {
          readonly response: {
            readonly content: {
              readonly "application/json": {
                readonly schema: {
                  readonly resolved: {
                    readonly properties: Readonly<Record<string, { readonly type?: readonly string[] | string }>>;
                    readonly required: readonly string[];
                  };
                };
              };
            };
          };
        }[];
      }[];
    };
    const addMember = descriptor.routes.find(({ operationId }) => operationId === "AddProjectTeamMember");
    const teamMember = addMember?.responses[0]?.response.content["application/json"].schema.resolved;

    assert.deepEqual(teamMember?.required, ["userId", "email", "displayName", "role", "joinedAt"]);
    assert.equal(teamMember?.properties.displayName?.type, "string");
    assert.deepEqual(teamMember?.properties.joinedAt?.type, ["null", "string"]);
  });
});

async function readContract(): Promise<PublicContractFixture> {
  const fixtureUrl = new URL(
    "../test/fixtures/public-contracts/saved-queries-and-branches.json",
    import.meta.url,
  );
  return JSON.parse(await readFile(fixtureUrl, "utf8")) as PublicContractFixture;
}
