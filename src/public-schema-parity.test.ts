import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import type {
  CreateCheckoutSessionRequest,
  PricingPlan,
  ProjectSavedQuerySnapshotPageRequest,
  ProjectSavedQuerySnapshotPageResponse,
  ProjectSavedQueryV2Response,
  UsageMetric,
} from "./models.js";
import type { SavedQueriesClient, SavedQueriesV3Client } from "./client.js";
import type { Ulid } from "./ids.js";
import type { TerraScaleResult } from "./result.js";

type Equal<Left, Right> =
  (<Value>() => Value extends Left ? 1 : 2) extends (<Value>() => Value extends Right ? 1 : 2)
    ? (<Value>() => Value extends Right ? 1 : 2) extends (<Value>() => Value extends Left ? 1 : 2)
      ? true
      : false
    : false;
type Assert<Condition extends true> = Condition;

type ExpectedPricingPlan = {
  readonly planId: Ulid;
  readonly name: string;
  readonly currency: string;
  readonly monthlyBaseCents: number;
  readonly annualBaseCents: number;
  readonly includedUsage: readonly UsageMetric[];
  readonly key: string;
  readonly provider: string;
  readonly monthlyPriceId: string | null;
  readonly annualPriceId: string | null;
  readonly monthlyPolarProductId: string | null;
  readonly annualPolarProductId: string | null;
  readonly monthlyStripePriceId: string | null;
  readonly annualStripePriceId: string | null;
  readonly monthlyTrialDays: number;
  readonly annualTrialDays: number;
  readonly visible: boolean;
  readonly maxRegions: number;
  readonly operationOverageCentsPerMillion: number;
  readonly storageOverageCentsPerGbRegion: number;
};

type ExpectedSnapshotRequest = {
  readonly cursor?: string;
  readonly take?: number;
};

type ExpectedCheckoutRequest = {
  readonly priceId?: string;
  readonly planId?: Ulid;
  readonly billingInterval?: "monthly" | "annual";
  readonly provider?: "polar" | "stripe";
  readonly successUrl?: string;
  readonly cancelUrl?: string;
  readonly customerEmail?: string;
  readonly quantity?: number;
};

type ExpectedSnapshotPage = {
  readonly items: readonly ProjectSavedQueryV2Response[];
  readonly nextCursor?: string | null;
};

type ExpectedSnapshotList = (
  projectId?: Ulid,
  query?: ExpectedSnapshotRequest,
) => Promise<TerraScaleResult<ExpectedSnapshotPage>>;

const staticSdkParity: readonly [
  Assert<Equal<PricingPlan, ExpectedPricingPlan>>,
  Assert<Equal<CreateCheckoutSessionRequest, ExpectedCheckoutRequest>>,
  Assert<Equal<ProjectSavedQuerySnapshotPageRequest, ExpectedSnapshotRequest>>,
  Assert<Equal<ProjectSavedQuerySnapshotPageResponse, ExpectedSnapshotPage>>,
  Assert<Equal<SavedQueriesV3Client["list"], ExpectedSnapshotList>>,
  Assert<Equal<SavedQueriesClient["v3"], SavedQueriesV3Client>>,
] = [true, true, true, true, true, true];
void staticSdkParity;

type PublicContractFixture = {
  readonly pricingPlan: {
    readonly required: readonly string[];
    readonly providerType: readonly string[];
    readonly nullableStringFields: readonly string[];
    readonly checkout: Readonly<Record<string, {
      readonly types?: readonly string[];
      readonly nullableUlid: boolean;
      readonly deprecated: boolean;
      readonly minimum?: number;
      readonly maximum?: number;
      readonly default?: number;
    }>>;
  };
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
};

describe("public contract fixture parity", () => {
  test("keeps pricing response requiredness/nullability and checkout input fields pinned", async () => {
    const contract = await readContract();

    assert.deepEqual(contract.pricingPlan.required, [
      "planId", "name", "currency", "monthlyBaseCents", "annualBaseCents", "includedUsage", "key",
      "provider", "monthlyPriceId", "annualPriceId", "monthlyPolarProductId", "annualPolarProductId",
      "monthlyStripePriceId", "annualStripePriceId", "monthlyTrialDays", "annualTrialDays", "visible",
      "maxRegions", "operationOverageCentsPerMillion", "storageOverageCentsPerGbRegion",
    ]);
    assert.deepEqual(contract.pricingPlan.providerType, ["string"]);
    assert.deepEqual(contract.pricingPlan.nullableStringFields, [
      "monthlyPriceId", "annualPriceId", "monthlyPolarProductId", "annualPolarProductId",
      "monthlyStripePriceId", "annualStripePriceId",
    ]);
    assert.deepEqual(contract.pricingPlan.checkout, {
      priceId: { types: ["string", "null"], nullableUlid: false, deprecated: true },
      quantity: { types: ["integer", "null"], nullableUlid: false, deprecated: false, minimum: 1, maximum: 1000, default: 1 },
      planId: { nullableUlid: true, deprecated: false },
      billingInterval: { types: ["string", "null"], nullableUlid: false, deprecated: false },
      provider: { types: ["string", "null"], nullableUlid: false, deprecated: false },
    });
  });

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
});

async function readContract(): Promise<PublicContractFixture> {
  const fixtureUrl = new URL("../test/fixtures/public-contracts/v3-billing.json", import.meta.url);
  return JSON.parse(await readFile(fixtureUrl, "utf8")) as PublicContractFixture;
}
