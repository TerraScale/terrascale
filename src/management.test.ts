import { expect, test } from "vitest";
import {
  requireTerraScaleManagement,
  terraScaleReleaseBlockers,
} from "./management.js";
import { requireTerraScaleAdministration } from "./admin.js";
import { TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
test("required management/admin capabilities remain explicit and closed until actual C06/C07 admission", () => {
  for (const operation of [
    requireTerraScaleManagement,
    requireTerraScaleAdministration,
  ]) {
    expect(operation).toThrow(TerraScaleCapabilityUnavailableError);
    try {
      operation();
    } catch (error) {
      expect((error as TerraScaleCapabilityUnavailableError).code).toBe(
        "management_contract_unavailable",
      );
    }
  }
  expect(terraScaleReleaseBlockers.management.required).toEqual(
    expect.arrayContaining([
      "schema/catalog plan/apply/status",
      "credential lifecycle",
      "resource provisioning",
      "administration",
    ]),
  );
});
