/** Internal ULID validation used by the opt-in local development runtime. */
export type Ulid = string;
export type OrganizationId = string;
export type ProjectId = string;
export type DatabaseId = string;
export type UserId = string;
export type InvitationId = string;
export type BackupId = string;
export type BackupPolicyId = string;
export type NotificationId = string;
export type AuditEventId = string;
export type RestoreId = string;

const ulidPattern = /^[0-9A-HJKMNP-TV-Z]{26}$/u;

export function isUlid(value: string): value is Ulid {
  return ulidPattern.test(value);
}

export function tryUlid(value: string): Ulid | undefined {
  return ulidPattern.test(value) ? value : undefined;
}

export function toUlid(value: string): Ulid {
  const ulid = tryUlid(value);
  if (ulid === undefined) throw new TypeError(`Not a valid ULID: ${value}`);
  return ulid;
}
