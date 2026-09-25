import type { OrganizationPlan } from "../models.js";

export const organizationPlans = [
  "free",
  "starter",
  "pro",
  "scale",
  "business",
  "enterprise",
] as const satisfies readonly OrganizationPlan[];

export function parseOrganizationPlan(value: unknown): OrganizationPlan | undefined {
  return typeof value === "string" && (organizationPlans as readonly string[]).includes(value)
    ? (value as OrganizationPlan)
    : undefined;
}
