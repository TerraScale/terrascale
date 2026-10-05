import { requireTerraScaleManagement } from "@terrascale/sdk/management";
import { requireTerraScaleBetterAuth } from "@terrascale/sdk/better-auth";
// Deliberate producer gate. Native document traffic alone cannot prove the
// required provisioning, credential, account and authentication lifecycle.
requireTerraScaleManagement();
requireTerraScaleBetterAuth();
