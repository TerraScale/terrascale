import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "node:test";

type OpenApiSchema = {
  readonly properties?: Readonly<Record<string, { readonly type?: string | readonly string[] }>>;
  readonly required?: readonly string[];
};

const pricingPlanFields = [
  "planId", "name", "currency", "monthlyBaseCents", "annualBaseCents", "includedUsage", "key",
  "provider", "monthlyPriceId", "annualPriceId", "monthlyPolarProductId", "annualPolarProductId",
  "monthlyStripePriceId", "annualStripePriceId", "monthlyTrialDays", "annualTrialDays", "visible",
  "maxRegions", "operationOverageCentsPerMillion", "storageOverageCentsPerGbRegion",
] as const;

describe("public OpenAPI schema parity", () => {
  test("keeps pricing responses and checkout inputs aligned with the published snapshot", async () => {
    const spec = await readSnapshot();
    const schemas = spec.components.schemas as Record<string, OpenApiSchema>;
    const pricingPlan = schemas.PricingPlanResponse;
    const checkout = schemas.CreateCheckoutSessionRequest;
    assert.ok(pricingPlan);
    assert.ok(checkout);

    assert.deepEqual(pricingPlan.required?.slice().sort(), [...pricingPlanFields].sort());
    assert.deepEqual(pricingPlan.properties?.provider?.type, "string");
    for (const field of [
      "monthlyPriceId", "annualPriceId", "monthlyPolarProductId", "annualPolarProductId",
      "monthlyStripePriceId", "annualStripePriceId",
    ]) {
      assert.deepEqual(pricingPlan.properties?.[field]?.type, ["null", "string"]);
    }
    for (const field of ["monthlyTrialDays", "annualTrialDays"]) {
      assert.equal(pricingPlan.required?.includes(field), true);
    }
    assert.deepEqual(Object.keys(checkout.properties ?? {}).sort(), [
      "billingInterval", "planId", "priceId", "provider", "quantity",
    ]);
  });

  test("keeps the v3 saved-query snapshot route, cursor, and page schema aligned", async () => {
    const spec = await readSnapshot();
    const operation = spec.paths["/api/v3/projects/{projectId}/saved-queries"].get;
    const schemas = spec.components.schemas as Record<string, OpenApiSchema>;
    const page = schemas.ProjectSavedQuerySnapshotPageResponse;
    assert.ok(page);

    assert.equal(operation.operationId, "ListProjectSavedQueriesV3");
    assert.deepEqual(operation.parameters.map((parameter: { readonly name: string }) => parameter.name), ["cursor", "take"]);
    assert.deepEqual(page.required, ["items"]);
    assert.deepEqual(page.properties?.nextCursor?.type, ["string", "null"]);
  });
});

async function readSnapshot(): Promise<any> {
  const snapshotUrl = new URL("../../Terrascale.Documentation/public/openapi/server.openapi.json", import.meta.url);
  return JSON.parse(await readFile(snapshotUrl, "utf8"));
}
