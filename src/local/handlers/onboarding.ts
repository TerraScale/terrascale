import type { LocalRequestContext } from "../context.js";
import { jsonResponse, matchLocalRoute, objectBody } from "../context.js";
import type * as Models from "../../models.js";
import type { OrganizationId, ProjectId, UserId } from "../../ids.js";

type OnboardingRecord = Pick<
  Models.OnboardingState,
  | "userId"
  | "email"
  | "journeyType"
  | "locale"
  | "invitationId"
  | "invitationOrganizationId"
  | "invitationProjectId"
  | "invitationAccepted"
  | "organizationId"
  | "organizationOwnerAssigned"
  | "projectId"
  | "projectOwnerAssigned"
  | "projectPrimaryRegions"
  | "projectBackupRegion"
  | "completed"
  | "status"
  | "nextStep"
  | "steps"
  | "updatedAt"
>;

const FALLBACK_USER = {
  userId: "01ARZ3NDEKTSV4RRFFQ69G5FAV" as UserId,
  email: "local@example.test",
} as const;

/** Implements the local onboarding state, organization, and project operations. */
export function handleLocalOnboarding(
  context: LocalRequestContext,
): Response | undefined | Promise<Response | undefined> {
  if (matchLocalRoute(context, "GET", "/api/v1/onboarding") !== undefined) {
    return jsonResponse(readState(context));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/onboarding/organization") !== undefined) {
    const state = readState(context);
    const updated = saveState(context, {
      ...state,
      organizationId: state.organizationId ?? (nextUlid(context) as OrganizationId),
      organizationOwnerAssigned: true,
      updatedAt: context.now,
    });
    return jsonResponse(recalculate(updated));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/onboarding/project") !== undefined) {
    const state = readState(context);
    const body = objectBody(context);
    const projectId = state.projectId ?? (nextUlid(context) as ProjectId);
    const regions = Array.isArray(body.regions)
      ? body.regions.filter((value): value is string => typeof value === "string")
      : state.projectPrimaryRegions;
    const updated = saveState(context, {
      ...state,
      projectId,
      projectOwnerAssigned: true,
      projectPrimaryRegions: regions as OnboardingRecord["projectPrimaryRegions"],
      projectBackupRegion: stringValue(body.backupRegion, state.projectBackupRegion),
      updatedAt: context.now,
    });
    return jsonResponse(recalculate(updated));
  }
  return undefined;
}

function readState(context: LocalRequestContext): OnboardingRecord {
  // The local adapter has one generic identity. It intentionally does not
  // inspect auth sessions or user records, which are outside this surface.
  const existing = context.store.get<OnboardingRecord>(
    "onboarding",
    "global",
    FALLBACK_USER.userId,
  );
  if (existing !== undefined) return saveState(context, recalculate(stripDomainState(existing)));
  return saveState(context, recalculate(initialState(context)));
}

function stripDomainState(state: OnboardingRecord): OnboardingRecord {
  const {
    userId,
    email,
    journeyType,
    locale,
    invitationId,
    invitationOrganizationId,
    invitationProjectId,
    invitationAccepted,
    organizationId,
    organizationOwnerAssigned,
    projectId,
    projectOwnerAssigned,
    projectPrimaryRegions,
    projectBackupRegion,
    completed,
    status,
    nextStep,
    steps,
    updatedAt,
  } = state;
  return {
    userId,
    email,
    journeyType,
    locale,
    invitationId: invitationId ?? null,
    invitationOrganizationId: invitationOrganizationId ?? null,
    invitationProjectId: invitationProjectId ?? null,
    invitationAccepted,
    organizationId: organizationId ?? null,
    organizationOwnerAssigned,
    projectId: projectId ?? null,
    projectOwnerAssigned,
    projectPrimaryRegions,
    projectBackupRegion,
    completed,
    status,
    nextStep,
    steps,
    updatedAt,
  };
}

function initialState(context: LocalRequestContext): OnboardingRecord {
  return {
    userId: FALLBACK_USER.userId,
    email: FALLBACK_USER.email,
    journeyType: "developer",
    locale: "en",
    invitationId: null,
    invitationOrganizationId: null,
    invitationProjectId: null,
    invitationAccepted: false,
    organizationId: null,
    organizationOwnerAssigned: false,
    projectId: null,
    projectOwnerAssigned: false,
    projectPrimaryRegions: [],
    projectBackupRegion: "",
    completed: false,
    status: "not-started",
    nextStep: "organization",
    steps: [],
    updatedAt: context.now,
  };
}

function saveState(context: LocalRequestContext, state: OnboardingRecord): OnboardingRecord {
  context.store.put("onboarding", "global", state.userId, state);
  return state;
}

function recalculate(state: OnboardingRecord): OnboardingRecord {
  const organization = state.organizationId !== null && state.organizationOwnerAssigned;
  const project = state.projectId !== null && state.projectOwnerAssigned;
  let nextStep: Models.OnboardingStep = "organization";
  if (organization) nextStep = "project";
  if (project) nextStep = "complete";
  const completed = nextStep === "complete";
  const keys: Exclude<Models.OnboardingStep, "complete">[] = [
    "organization",
    "project",
    "invitation",
  ];
  const currentIndex = keys.indexOf(nextStep as Exclude<Models.OnboardingStep, "complete">);
  const steps = keys.map((key, index) => ({
    key,
    state: index < currentIndex || completed
      ? "completed"
      : index === currentIndex
        ? "current"
        : "pending",
  } as const));
  const status: Models.OnboardingStatus = completed
    ? "completed"
    : organization
      ? "ready-for-project"
      : "not-started";
  return { ...state, completed, nextStep, status, steps, updatedAt: state.updatedAt };
}

function nextUlid(context: LocalRequestContext): string {
  return context.store.nextUlid();
}

function stringValue(value: unknown, fallback: string): string {
  return typeof value === "string" && value.length > 0 ? value : fallback;
}
