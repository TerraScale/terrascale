import type { LocalRequestContext } from "../context.js";
import { jsonResponse, matchLocalRoute, objectBody } from "../context.js";
import type * as Models from "../../models.js";
import type { OrganizationId, ProjectId, UserId } from "../../ids.js";

type UserShape = { userId: UserId; email: string; name: string; phoneNumber: string };
type SessionShape = { userId: UserId; accessToken: string; revoked: boolean };
type OnboardingRecord = Models.OnboardingState & { paymentSessionId?: string };

const FALLBACK_USER: UserShape = {
  userId: "01ARZ3NDEKTSV4RRFFQ69G5FAV" as UserId,
  email: "local@example.test",
  name: "Local User",
  phoneNumber: "",
};

/** Implements all public /api/v1/onboarding operations for the local host. */
export function handleLocalOnboarding(context: LocalRequestContext): Response | undefined | Promise<Response | undefined> {
  if (matchLocalRoute(context, "GET", "/api/v1/onboarding") !== undefined) {
    return jsonResponse(readState(context));
  }
  if (matchLocalRoute(context, "PATCH", "/api/v1/onboarding/account-information") !== undefined) {
    const state = readState(context);
    const body = objectBody(context);
    const updated = saveState(context, {
      ...state,
      displayName: stringValue(body.displayName, state.displayName),
      phoneNumber: stringValue(body.phoneNumber, state.phoneNumber),
      accountInformationCompleted: true,
      updatedAt: context.now,
    });
    return jsonResponse(recalculate(updated));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/onboarding/payment-method/checkout-session") !== undefined) {
    const body = objectBody(context);
    const state = readState(context);
    const plan = planValue(body.plan, state.selectedPlan);
    const sessionId = `local-checkout-${short(`${state.userId}:${plan}`)}`;
    saveState(context, { ...state, selectedPlan: plan, paymentSessionId: sessionId, updatedAt: context.now });
    return jsonResponse({ checkoutUrl: `https://local.terrascale.test/onboarding/payment/${sessionId}`, sessionId } satisfies Models.OnboardingPaymentSession);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/onboarding/payment-method/register") !== undefined) {
    const state = readState(context);
    const body = objectBody(context);
    const updated = saveState(context, { ...state, paymentMethodRegistered: true, paymentSessionId: stringValue(body.sessionId, state.paymentSessionId ?? ""), updatedAt: context.now });
    return jsonResponse(recalculate(updated));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/onboarding/payment-method/validate") !== undefined) {
    const state = readState(context);
    const updated = saveState(context, { ...state, paymentMethodValidated: true, paymentValidationStatus: "approved", retryState: "none", updatedAt: context.now });
    return jsonResponse(recalculate(updated));
  }
  if (matchLocalRoute(context, "PUT", "/api/v1/onboarding/mfa-decision") !== undefined) {
    const state = readState(context);
    const body = objectBody(context);
    const updated = saveState(context, { ...state, mfaDecision: body.configureLater === true ? "later" : "configured", updatedAt: context.now });
    return jsonResponse(recalculate(updated));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/onboarding/organization") !== undefined) {
    const state = readState(context);
    const body = objectBody(context);
    const organizationId = state.organizationId ?? nextUlid(context) as OrganizationId;
    const updated = saveState(context, { ...state, organizationId, organizationOwnerAssigned: true, paymentMethodLinkedToOrganization: state.paymentMethodRegistered, subscriptionOrTrialActive: true, updatedAt: context.now, displayName: stringValue(body.name, state.displayName), selectedPlan: planValue(body.plan, state.selectedPlan) });
    return jsonResponse(recalculate(updated));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/onboarding/project") !== undefined) {
    const state = readState(context);
    const body = objectBody(context);
    const projectId = state.projectId ?? nextUlid(context) as ProjectId;
    const regions = Array.isArray(body.regions) ? body.regions.filter((value): value is string => typeof value === "string") : state.projectPrimaryRegions;
    const updated = saveState(context, { ...state, projectId, projectOwnerAssigned: true, projectPrimaryRegions: regions as Models.OnboardingState["projectPrimaryRegions"], projectBackupRegion: stringValue(body.backupRegion, state.projectBackupRegion), updatedAt: context.now });
    return jsonResponse(recalculate(updated));
  }
  return undefined;
}

function readState(context: LocalRequestContext): OnboardingRecord {
  const defaultId = context.store.getMeta<UserId>("local.defaultUserId") ?? "";
  const authorization = context.request.headers.get("authorization")?.replace(/^Bearer\s+/iu, "") ?? "";
  const sessions = context.store.list<SessionShape>("auth:sessions", "global");
  const session = sessions.find((entry) => entry.accessToken === authorization && !entry.revoked);
  const userId = session?.userId ?? defaultId;
  const candidate = context.store.get<UserShape>("auth:users", "global", userId);
  const user = candidate ?? FALLBACK_USER;
  const existing = context.store.get<OnboardingRecord>("onboarding", "global", user.userId);
  if (existing !== undefined) return existing;
  const initial: OnboardingRecord = { ...initialState(context), userId: user.userId, email: user.email, displayName: user.name, phoneNumber: user.phoneNumber };
  return saveState(context, recalculate(initial));
}

function initialState(context: LocalRequestContext): OnboardingRecord {
  return {
    userId: FALLBACK_USER.userId, email: FALLBACK_USER.email, displayName: FALLBACK_USER.name, phoneNumber: FALLBACK_USER.phoneNumber, journeyType: "developer", selectedPlan: "developer", locale: "en",
    invitationId: null, invitationOrganizationId: null, invitationProjectId: null, invitationAccepted: false, retryState: "none", accountInformationCompleted: false, mfaDecision: "pending",
    paymentMethodRegistered: false, paymentMethodValidated: false, paymentValidationStatus: "not-started", organizationId: null, organizationOwnerAssigned: false, paymentMethodLinkedToOrganization: false,
    subscriptionOrTrialActive: false, projectId: null, projectOwnerAssigned: false, projectPrimaryRegions: [], projectBackupRegion: "", completed: false, status: "not-started", nextStep: "account-information",
    steps: [], updatedAt: context.now,
  } satisfies OnboardingRecord;
}

function saveState(context: LocalRequestContext, state: OnboardingRecord): OnboardingRecord {
  context.store.put("onboarding", "global", state.userId, state);
  return state;
}

function recalculate(state: OnboardingRecord): OnboardingRecord {
  const account = state.accountInformationCompleted;
  const mfa = state.mfaDecision !== "pending";
  const paid = state.selectedPlan !== "developer";
  const organization = state.organizationId !== null && state.organizationOwnerAssigned;
  const project = state.projectId !== null && state.projectOwnerAssigned;
  let nextStep: Models.OnboardingStep = "account-information";
  if (account) nextStep = "mfa";
  if (account && mfa) nextStep = paid && !state.paymentMethodRegistered ? "payment-method" : paid && !state.paymentMethodValidated ? "payment-validation" : "organization";
  if (organization) nextStep = "project";
  if (project) nextStep = "complete";
  const completed = nextStep === "complete";
  const keys: Exclude<Models.OnboardingStep, "complete">[] = ["account-information", "mfa", "organization", "payment-method", "payment-validation", "project", "invitation"];
  const currentIndex = keys.indexOf(nextStep as Exclude<Models.OnboardingStep, "complete">);
  const steps = keys.map((key, index) => ({ key, state: index < currentIndex || completed ? "completed" : index === currentIndex ? "current" : "pending" } as const));
  const status: Models.OnboardingStatus = completed ? "completed" : !account ? "not-started" : nextStep === "payment-validation" ? "awaiting-validation" : nextStep === "project" ? "ready-for-project" : "in-progress";
  return { ...state, paymentMethodLinkedToOrganization: state.paymentMethodLinkedToOrganization || organization, subscriptionOrTrialActive: state.subscriptionOrTrialActive || (!paid && organization), completed, nextStep, status, steps, updatedAt: state.updatedAt };
}

function nextUlid(context: LocalRequestContext): string {
  return context.store.nextUlid();
}
function stringValue(value: unknown, fallback: string): string {
  return typeof value === "string" && value.length > 0 ? value : fallback;
}
function planValue(value: unknown, fallback: Models.OnboardingPlan): Models.OnboardingPlan {
  return value === "starter" || value === "pro" || value === "developer" ? value : fallback;
}
function short(value: string): string {
  let hash = 2166136261;
  for (const character of value) hash = Math.imul(hash ^ character.charCodeAt(0), 16777619);
  return (hash >>> 0).toString(36);
}
