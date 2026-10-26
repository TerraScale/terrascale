import { requireTerraScaleBetterAuth } from "@terrascale/sdk/better-auth";
// Management operations have a public project-scoped implementation. The full
// account journey still requires the independent Better Auth producer contract.
requireTerraScaleBetterAuth();
