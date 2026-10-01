import { requireTerraScaleManagement } from "terrascale/management";
import { requireTerraScaleBetterAuth } from "terrascale/better-auth";
// Deliberate producer gate. Native document traffic alone cannot prove the
// required provisioning, credential, account and authentication lifecycle.
requireTerraScaleManagement();
requireTerraScaleBetterAuth();
