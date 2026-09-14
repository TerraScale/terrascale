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

export type Brand<TValue, TBrand extends string> = TValue & {
  readonly [terraScaleBrand]: TBrand;
};

export type Ulid = Brand<string, "Ulid">;

export type OrganizationId = Ulid;
export type ProjectId = Ulid;
export type DatabaseId = Ulid;
export type EnvironmentId = Ulid;
export type RepositoryId = Ulid;
export type DocumentId = Ulid;
export type UserId = Ulid;
export type InvitationId = Ulid;
export type BackupId = Ulid;
export type BackupPolicyId = Ulid;
export type QueueId = Ulid;
export type NotificationId = Ulid;
export type AuditEventId = Ulid;
export type RestoreId = Ulid;

// A canonical 128-bit ULID cannot start above 7. Accepting 8-Z would admit
// values larger than 2^128-1 even though the remaining characters are valid
// Crockford Base32.
const ulidPattern = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;

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
