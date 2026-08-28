import type { LocalJsonObject, LocalRequestContext } from "../context.js";
import {
  emptyResponse,
  jsonResponse,
  matchLocalRoute,
  objectBody,
} from "../context.js";
import type * as Models from "../../models.js";
import type { OrganizationId, ProjectId, Ulid, UserId } from "../../ids.js";

/**
 * Local development implementation of the Authentication public contract.
 *
 * The local host deliberately uses ordinary bearer authentication semantics:
 * tokens issued here are only opaque, deterministic development values.  No
 * private header or development-only bypass is accepted.
 */

type UserRecord = {
  userId: UserId;
  email: string;
  name: string;
  phoneNumber: string;
  avatarUrl: string;
  personalOrgId: OrganizationId;
  activeOrgId: OrganizationId;
  emailVerified: boolean;
  passwordChangedAt?: string;
};

type SessionRecord = {
  sessionId: Ulid;
  userId: UserId;
  accessToken: string;
  refreshToken: string;
  deviceType: string;
  browser: string;
  os: string;
  ipAddress: string;
  location: string;
  createdAt: string;
  lastActiveAt: string;
  revoked: boolean;
};

type PendingRecord = {
  userId: UserId;
  email: string;
  kind: "signup" | "magic-link" | "email-change" | "reset" | "external";
  value?: string;
  createdAt: string;
  factorId?: Ulid;
};

type FactorRecord = Models.MfaFactor & { userId: UserId; active: boolean };

const DEFAULT_EMAIL = "local@example.test";

export function handleLocalAuth(context: LocalRequestContext): Response | undefined | Promise<Response | undefined> {
  const body = objectBody(context);

  // Account identity and billing guard.
  if (matchLocalRoute(context, "GET", "/api/v1/auth/me") !== undefined) {
    return jsonResponse(authMe(context, currentUser(context)));
  }
  if (matchLocalRoute(context, "GET", "/api/v1/auth/me/billing") !== undefined) {
    return jsonResponse(currentBilling(context, currentUser(context)));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/me/billing/guard") !== undefined) {
    return jsonResponse(billingGuard(context, currentUser(context), body));
  }

  // Password and external account flows.
  if (matchLocalRoute(context, "POST", "/api/v1/auth/password/login") !== undefined) {
    const user = ensureUser(context, stringValue(body.email, DEFAULT_EMAIL), body);
    return jsonResponse({ status: "Success", authResponse: issueTokens(context, user) } satisfies Models.PasswordLoginResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/password/signup") !== undefined) {
    const email = stringValue(body.email, DEFAULT_EMAIL);
    const user = ensureUser(context, email, body);
    const pendingAuthenticationToken = token("signup", email);
    put(context, "auth:pending", pendingAuthenticationToken, {
      userId: user.userId,
      email: user.email,
      kind: "signup",
      createdAt: context.now,
    } satisfies PendingRecord);
    return jsonResponse({
      success: true,
      requiresEmailVerification: true,
      pendingAuthenticationToken,
      userId: user.userId,
      email: user.email,
    } satisfies Models.SignupResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/password/verify-email") !== undefined) {
    return jsonResponse(verifyPendingSignup(context, body));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/password/forgot-password") !== undefined) {
    const email = stringValue(body.email, DEFAULT_EMAIL);
    ensureUser(context, email, body);
    put(context, "auth:pending", token("reset", email), {
      userId: ensureUser(context, email, body).userId,
      email,
      kind: "reset",
      createdAt: context.now,
    } satisfies PendingRecord);
    return emptyResponse();
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/password/reset-password") !== undefined) {
    const pending = get<PendingRecord>(context, "auth:pending", stringValue(body.token, ""));
    if (pending !== undefined) {
      const user = get<UserRecord>(context, "auth:users", pending.userId);
      if (user !== undefined) saveUser(context, { ...user, passwordChangedAt: context.now });
      del(context, "auth:pending", stringValue(body.token, ""));
    }
    return emptyResponse();
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/password/mfa/verify") !== undefined) {
    return jsonResponse(passwordMfaResult(context, body));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/external-login/begin") !== undefined) {
    const provider = stringValue(body.provider, "google");
    const state = stringValue(body.state, "local-state");
    return jsonResponse({
      authorizeUrl: `https://local.terrascale.test/auth/external/${encodeURIComponent(provider)}?state=${encodeURIComponent(state)}`,
    } satisfies Models.ExternalLoginBeginResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/external-login/complete") !== undefined) {
    const email = stringValue(body.email, DEFAULT_EMAIL);
    return jsonResponse({ status: "Success", authResponse: issueTokens(context, ensureUser(context, email, body)) } satisfies Models.PasswordLoginResponse);
  }

  // MFA.
  if (matchLocalRoute(context, "POST", "/api/v1/auth/mfa/totp/enroll") !== undefined) {
    const user = currentUser(context);
    const factorId = nextUlid(context);
    const challengeId = nextUlid(context);
    const secret = `LOCAL-ONLY-TOTP-${short(user.email)}`;
    put(context, "auth:factors", factorId, { id: factorId, type: "totp", createdAt: context.now, userId: user.userId, active: false } satisfies FactorRecord);
    put(context, "auth:pending", challengeId, { userId: user.userId, email: user.email, kind: "external", factorId, createdAt: context.now } satisfies PendingRecord);
    return jsonResponse({ factorId, type: "totp", qrCode: `otpauth://totp/TerraScale:${encodeURIComponent(user.email)}?secret=${secret}&issuer=TerraScale`, secret, challengeId } satisfies Models.EnrollTotpResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/mfa/totp/enroll/verify") !== undefined) {
    const challenge = get<PendingRecord>(context, "auth:pending", stringValue(body.challengeId, ""));
    if (challenge?.factorId !== undefined) {
      const factor = get<FactorRecord>(context, "auth:factors", challenge.factorId);
      if (factor !== undefined) put(context, "auth:factors", factor.id, { ...factor, active: true });
      del(context, "auth:pending", stringValue(body.challengeId, ""));
    }
    return jsonResponse({ success: true, accessToken: `local-access-${short(currentUser(context).email)}`, expiresIn: 3600, verifiedAt: context.now } satisfies Models.VerifyTotpEnrollmentResponse);
  }
  if (matchLocalRoute(context, "GET", "/api/v1/auth/mfa/factors") !== undefined) {
    const user = currentUser(context);
    return jsonResponse({ factors: list<FactorRecord>(context, "auth:factors").filter((factor) => factor.userId === user.userId && factor.active).map(({ id, type, createdAt }) => ({ id, type, createdAt: createdAt ?? null })) } satisfies Models.ListMfaFactorsResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/mfa/recovery-codes/generate") !== undefined) {
    const codes = ["local-recovery-01", "local-recovery-02", "local-recovery-03", "local-recovery-04"];
    put(context, "auth:recovery", currentUser(context).userId, codes);
    return jsonResponse({ codes, generatedAt: context.now } satisfies Models.GenerateRecoveryCodesResponse);
  }
  const factorRoute = matchLocalRoute(context, "DELETE", "/api/v1/auth/mfa/factors/{factorId}");
  if (factorRoute !== undefined) {
    const factorId = factorRoute.factorId ?? "";
    const factor = get<FactorRecord>(context, "auth:factors", factorId);
    if (factor !== undefined && factor.userId === currentUser(context).userId) del(context, "auth:factors", factorId);
    return jsonResponse({ success: true, verifiedAt: context.now } satisfies Models.VerifyTotpEnrollmentResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/password/change-password") !== undefined) {
    const user = currentUser(context);
    saveUser(context, { ...user, passwordChangedAt: context.now });
    return jsonResponse({ success: true } satisfies Models.ChangePasswordResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/passwordless/magic-link") !== undefined) {
    const email = stringValue(body.email, DEFAULT_EMAIL);
    const user = ensureUser(context, email, body);
    put(context, "auth:pending", token("magic", email), { userId: user.userId, email, kind: "magic-link", createdAt: context.now } satisfies PendingRecord);
    return jsonResponse({ success: true, message: "A local sign-in link is ready.", expiresAt: new Date(Date.parse(context.now) + 15 * 60_000).toISOString() } satisfies Models.EmailSignInResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/passwordless/magic-link/complete") !== undefined) {
    const supplied = stringValue(body.token ?? body.code, "");
    const pending = get<PendingRecord>(context, "auth:pending", supplied) ?? get<PendingRecord>(context, "auth:pending", token("magic", stringValue(body.email, DEFAULT_EMAIL)));
    const user = pending === undefined ? currentUser(context) : get<UserRecord>(context, "auth:users", pending.userId) ?? currentUser(context);
    return jsonResponse({ success: true, authResponse: issueTokens(context, user) } satisfies Models.CompleteEmailSignInResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/refresh") !== undefined) {
    const refresh = stringValue(body.refreshToken, "");
    const session = list<SessionRecord>(context, "auth:sessions").find((entry) => entry.refreshToken === refresh && !entry.revoked);
    const user = session === undefined ? currentUser(context) : get<UserRecord>(context, "auth:users", session.userId) ?? currentUser(context);
    return jsonResponse(issueTokens(context, user));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/logout") !== undefined) {
    const session = currentSession(context);
    if (session !== undefined) del(context, "auth:sessions", session.sessionId);
    else for (const entry of list<SessionRecord>(context, "auth:sessions")) if (entry.userId === currentUser(context).userId) del(context, "auth:sessions", entry.sessionId);
    return emptyResponse();
  }

  // Account sessions and profile.
  if (matchLocalRoute(context, "GET", "/api/v1/account/sessions") !== undefined) {
    const user = currentUser(context);
    const current = currentSession(context)?.sessionId;
    const sessions = list<SessionRecord>(context, "auth:sessions").filter((entry) => entry.userId === user.userId && !entry.revoked).map((entry) => ({
      sessionId: entry.sessionId, deviceType: entry.deviceType, browser: entry.browser, os: entry.os, ipAddress: entry.ipAddress, location: entry.location,
      createdAt: entry.createdAt, lastActiveAt: entry.lastActiveAt, isCurrent: entry.sessionId === current,
    } satisfies Models.AccountSession));
    return jsonResponse({ sessions } satisfies Models.ListAccountSessionsResponse);
  }
  const sessionRoute = matchLocalRoute(context, "DELETE", "/api/v1/account/sessions/{sessionId}");
  if (sessionRoute !== undefined) {
    const id = sessionRoute.sessionId ?? "";
    const session = get<SessionRecord>(context, "auth:sessions", id);
    if (session?.userId === currentUser(context).userId) del(context, "auth:sessions", id);
    return emptyResponse();
  }
  if (matchLocalRoute(context, "DELETE", "/api/v1/account/sessions") !== undefined) {
    const current = currentSession(context)?.sessionId;
    for (const session of list<SessionRecord>(context, "auth:sessions")) if (session.userId === currentUser(context).userId && session.sessionId !== current) del(context, "auth:sessions", session.sessionId);
    return emptyResponse();
  }
  if (matchLocalRoute(context, "PATCH", "/api/v1/users/me/profile") !== undefined) {
    const user = currentUser(context);
    const name = stringValue(body.displayName, user.name);
    const phoneNumber = stringValue(body.phoneNumber, user.phoneNumber);
    saveUser(context, { ...user, name, phoneNumber });
    return jsonResponse({ success: true, name, phoneNumber, updatedAt: context.now } satisfies Models.UpdateProfileResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/auth/switch-organization") !== undefined) {
    const user = currentUser(context);
    const activeOrgId = stringValue(body.organizationId, user.activeOrgId) as OrganizationId;
    const updated = { ...user, activeOrgId };
    saveUser(context, updated);
    return jsonResponse(issueTokens(context, updated));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/users/me/email/change") !== undefined) {
    const user = currentUser(context);
    const newEmail = stringValue(body.newEmail, user.email).trim().toLowerCase();
    put(context, "auth:pending", token("email-change", newEmail), { userId: user.userId, email: newEmail, value: "123456", kind: "email-change", createdAt: context.now } satisfies PendingRecord);
    return jsonResponse({ success: true, message: "A local verification code is ready.", expiresAt: new Date(Date.parse(context.now) + 15 * 60_000).toISOString() } satisfies Models.InitiateEmailChangeResponse);
  }
  if (matchLocalRoute(context, "POST", "/api/v1/users/me/email/verify") !== undefined) {
    const user = currentUser(context);
    const newEmail = stringValue(body.newEmail, user.email).trim().toLowerCase();
    const pending = get<PendingRecord>(context, "auth:pending", token("email-change", newEmail));
    const updated = { ...user, email: newEmail };
    saveUser(context, updated, user.email);
    if (pending !== undefined) del(context, "auth:pending", token("email-change", newEmail));
    return jsonResponse({ success: true, error: "" } satisfies Models.VerifyEmailChangeResponse);
  }

  // Project-scoped authentication.
  const project = matchProjectAuth(context);
  if (project !== undefined) {
    const projectId = project.params.projectId as ProjectId;
    if (project.kind === "providers") {
      const provider: Models.ProjectAuthProvider = {
        projectId, provider: "password", protocol: "password", displayName: "Password", enabled: true, builtIn: true,
        clientId: "local-password", hasClientSecret: false, authorizationEndpoint: "", tokenEndpoint: "", userInfoEndpoint: "", redirectUri: "",
        scopes: [], samlServiceProviderEntityId: "", samlIdentityProviderEntityId: "", samlSingleSignOnUrl: "", hasSamlSigningCertificate: false,
        samlAssertionConsumerServiceUrl: "", createdAt: context.now, updatedAt: context.now,
      };
      return jsonResponse({ providers: [provider], nextCursor: "" } satisfies Models.ListProjectAuthProvidersResponse);
    }
    if (project.kind === "register") {
      const email = stringValue(body.email, DEFAULT_EMAIL);
      const user = ensureUser(context, email, body);
      const pendingAuthenticationToken = token(`project-signup-${projectId}`, email);
      put(context, "auth:pending", pendingAuthenticationToken, { userId: user.userId, email, kind: "signup", createdAt: context.now } satisfies PendingRecord);
      return jsonResponse({ success: true, requiresEmailVerification: true, pendingAuthenticationToken, userId: user.userId, email } satisfies Models.SignupResponse);
    }
    if (project.kind === "verify-email") return jsonResponse(verifyPendingSignup(context, body));
    if (project.kind === "resend" || project.kind === "forgot" || project.kind === "reset" || project.kind === "signout" || project.kind === "magic-request") return emptyResponse();
    if (project.kind === "login" || project.kind === "mfa") return jsonResponse({ status: "Success", authResponse: issueTokens(context, ensureUser(context, stringValue(body.email, DEFAULT_EMAIL), body)) } satisfies Models.PasswordLoginResponse);
    if (project.kind === "magic-complete" || project.kind === "refresh") return jsonResponse(issueTokens(context, currentUser(context)));
    if (project.kind === "recovery") {
      return jsonResponse({ success: true, accessToken: `local-access-${short(currentUser(context).email)}`, refreshToken: `local-refresh-${short(currentUser(context).email)}`, expiresIn: 3600 } satisfies Models.VerifyRecoveryCodeResponse);
    }
  }

  // Avoid accidentally claiming unrelated local routes.
  return undefined;
}

type ProjectRoute = { kind: "providers" | "register" | "verify-email" | "resend" | "login" | "mfa" | "forgot" | "reset" | "magic-request" | "magic-complete" | "refresh" | "signout" | "recovery"; params: Readonly<Record<string, string>> };

function matchProjectAuth(context: LocalRequestContext): ProjectRoute | undefined {
  const entries: readonly [ProjectRoute["kind"], string, string][] = [
    ["register", "POST", "/api/v1/projects/{projectId}/auth/signup"], ["verify-email", "POST", "/api/v1/projects/{projectId}/auth/verify-email"],
    ["resend", "POST", "/api/v1/projects/{projectId}/auth/resend-verification"], ["login", "POST", "/api/v1/projects/{projectId}/auth/login"],
    ["mfa", "POST", "/api/v1/projects/{projectId}/auth/password/mfa/verify"], ["forgot", "POST", "/api/v1/projects/{projectId}/auth/forgot-password"],
    ["reset", "POST", "/api/v1/projects/{projectId}/auth/reset-password"], ["magic-request", "POST", "/api/v1/projects/{projectId}/auth/passwordless/magic-link"],
    ["magic-complete", "POST", "/api/v1/projects/{projectId}/auth/passwordless/magic-link/complete"], ["refresh", "POST", "/api/v1/projects/{projectId}/auth/refresh"],
    ["signout", "POST", "/api/v1/projects/{projectId}/auth/logout"], ["providers", "GET", "/api/v1/projects/{projectId}/auth/providers"],
    ["recovery", "POST", "/api/v1/projects/{projectId}/auth/mfa/recovery-codes/verify"],
  ];
  for (const [kind, method, template] of entries) {
    const params = matchLocalRoute(context, method, template);
    if (params !== undefined) return { kind, params };
  }
  return undefined;
}

function currentUser(context: LocalRequestContext): UserRecord {
  const tokenValue = context.request.headers.get("authorization")?.replace(/^Bearer\s+/iu, "") ?? "";
  const session = list<SessionRecord>(context, "auth:sessions").find((entry) => entry.accessToken === tokenValue && !entry.revoked);
  if (session !== undefined) {
    const user = get<UserRecord>(context, "auth:users", session.userId);
    if (user !== undefined) return user;
  }
  const defaultId = context.store.getMeta<UserId>("local.defaultUserId");
  if (defaultId !== undefined) {
    const user = get<UserRecord>(context, "auth:users", defaultId);
    if (user !== undefined) return user;
  }
  return ensureUser(context, DEFAULT_EMAIL, {});
}

function currentSession(context: LocalRequestContext): SessionRecord | undefined {
  const tokenValue = context.request.headers.get("authorization")?.replace(/^Bearer\s+/iu, "") ?? "";
  return list<SessionRecord>(context, "auth:sessions").find((entry) => entry.accessToken === tokenValue && !entry.revoked);
}

function ensureUser(context: LocalRequestContext, rawEmail: string, body: LocalJsonObject): UserRecord {
  const email = rawEmail.trim().toLowerCase() || DEFAULT_EMAIL;
  const existing = get<UserRecord>(context, "auth:usersByEmail", email);
  if (existing !== undefined) return existing;
  const user: UserRecord = {
    userId: nextUlid(context) as UserId, email, name: stringValue(body.displayName ?? body.fullName, email.split("@")[0] ?? "Local User"),
    phoneNumber: stringValue(body.phoneNumber, ""), avatarUrl: "", personalOrgId: nextUlid(context) as OrganizationId, activeOrgId: nextUlid(context) as OrganizationId, emailVerified: false,
  };
  put(context, "auth:users", user.userId, user);
  put(context, "auth:usersByEmail", email, user);
  if (context.store.getMeta<UserId>("local.defaultUserId") === undefined) context.store.setMeta("local.defaultUserId", user.userId);
  return user;
}

function saveUser(context: LocalRequestContext, user: UserRecord, previousEmail?: string): void {
  put(context, "auth:users", user.userId, user);
  if (previousEmail !== undefined && previousEmail !== user.email) del(context, "auth:usersByEmail", previousEmail);
  put(context, "auth:usersByEmail", user.email, user);
}

function issueTokens(context: LocalRequestContext, user: UserRecord): Models.AuthTokenResponse {
  const accessToken = `local-access-${short(user.email)}`;
  const refreshToken = `local-refresh-${short(user.email)}`;
  const previous = list<SessionRecord>(context, "auth:sessions").find((entry) => entry.userId === user.userId && entry.accessToken === accessToken);
  const session: SessionRecord = previous ?? { sessionId: nextUlid(context), userId: user.userId, accessToken, refreshToken, deviceType: "browser", browser: "local", os: "local", ipAddress: "127.0.0.1", location: "local", createdAt: context.now, lastActiveAt: context.now, revoked: false };
  put(context, "auth:sessions", session.sessionId, { ...session, lastActiveAt: context.now, revoked: false });
  return {
    accessToken, refreshToken, tokenType: "Bearer", expiresIn: 3600, userId: user.userId, email: user.email, name: user.name, phoneNumber: user.phoneNumber, avatarUrl: user.avatarUrl,
    personalOrgId: user.personalOrgId, activeOrgId: user.activeOrgId, permissions: ["*"], roles: ["User"], shouldPromptMfa: false, mfaEnabled: false, requiresOrganizationSelection: false,
  } satisfies Models.AuthTokenResponse;
}

function authMe(_context: LocalRequestContext, user: UserRecord): Models.AuthMeResponse {
  return { userId: user.userId, email: user.email, name: user.name, phoneNumber: user.phoneNumber, avatarUrl: user.avatarUrl, personalOrgId: user.personalOrgId, activeOrgId: user.activeOrgId, currentOrgId: user.activeOrgId, permissions: ["*"], roles: ["User"], preferences: { theme: "system", timezone: "UTC", emailNotifications: true, language: "en" } } satisfies Models.AuthMeResponse;
}

function currentBilling(_context: LocalRequestContext, user: UserRecord): Models.CurrentOrganizationBilling {
  return { organizationId: user.activeOrgId, status: "active", isActive: true, isTrialing: true, provider: "local", customerId: null, subscriptionId: null, planId: "local-developer", planName: "Developer", planType: "developer", amount: 0, currency: "USD", currentPeriodStart: null, currentPeriodEnd: null, billingInterval: "monthly", features: [], limits: {}, billingRoles: ["owner"] } satisfies Models.CurrentOrganizationBilling;
}

function billingGuard(context: LocalRequestContext, user: UserRecord, body: LocalJsonObject): Models.BillingGuardResponse {
  const billing = currentBilling(context, user);
  const allowTrialing = body.allowTrialing !== false;
  const reasons = body.requireActiveSubscription === true && !billing.isActive ? ["An active subscription is required."] : [];
  if (body.requireActiveSubscription === true && allowTrialing && billing.isTrialing) reasons.length = 0;
  return { allowed: reasons.length === 0, ...(reasons.length > 0 ? { reason: reasons[0] } : {}), reasons, missingRoles: [], missingFeatures: [], billingActionRequired: false, status: billing.status, planId: billing.planId ?? "local-developer", planType: billing.planType ?? "developer" } satisfies Models.BillingGuardResponse;
}

function verifyPendingSignup(context: LocalRequestContext, body: LocalJsonObject): Models.SignupResponse {
  const pendingToken = stringValue(body.pendingAuthenticationToken, "");
  const pending = get<PendingRecord>(context, "auth:pending", pendingToken);
  const user = pending === undefined ? ensureUser(context, DEFAULT_EMAIL, body) : get<UserRecord>(context, "auth:users", pending.userId) ?? ensureUser(context, pending.email, body);
  const verified = { ...user, emailVerified: true };
  saveUser(context, verified);
  return { success: true, requiresEmailVerification: false, pendingAuthenticationToken: pendingToken || token("signup", user.email), userId: verified.userId, email: verified.email } satisfies Models.SignupResponse;
}

function passwordMfaResult(context: LocalRequestContext, body: LocalJsonObject): Models.PasswordLoginResponse {
  const pending = get<PendingRecord>(context, "auth:pending", stringValue(body.pendingAuthenticationToken, ""));
  const user = pending === undefined ? currentUser(context) : get<UserRecord>(context, "auth:users", pending.userId) ?? currentUser(context);
  return { status: "Success", authResponse: issueTokens(context, user) } satisfies Models.PasswordLoginResponse;
}

function get<T>(context: LocalRequestContext, namespace: string, key: string): T | undefined {
  return context.store.get<T>(namespace, "global", key);
}
function list<T>(context: LocalRequestContext, namespace: string): readonly T[] {
  return context.store.list<T>(namespace, "global");
}
function put<T>(context: LocalRequestContext, namespace: string, key: string, value: T): void {
  context.store.put(namespace, "global", key, value);
}
function del(context: LocalRequestContext, namespace: string, key: string): void {
  context.store.delete(namespace, "global", key);
}
function nextUlid(context: LocalRequestContext): Ulid {
  return context.store.nextUlid() as Ulid;
}
function token(prefix: string, value: string): string {
  return `local-${prefix}-${short(value)}`;
}
function short(value: string): string {
  let hash = 2166136261;
  for (const character of value) hash = Math.imul(hash ^ character.charCodeAt(0), 16777619);
  return (hash >>> 0).toString(36);
}
function stringValue(value: unknown, fallback: string): string {
  return typeof value === "string" && value.length > 0 ? value : fallback;
}
