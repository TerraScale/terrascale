declare const terraScaleBrand: unique symbol;

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

const ulidPattern = /^[0-9A-HJKMNP-TV-Z]{26}$/;

export function isUlid(value: string): value is Ulid {
  return ulidPattern.test(value);
}

export function tryUlid(value: string): Ulid | undefined {
  const normalized = value.trim().toUpperCase();
  return ulidPattern.test(normalized) ? (normalized as Ulid) : undefined;
}

export function toUlid(value: string): Ulid {
  const ulid = tryUlid(value);

  if (ulid === undefined) {
    throw new TypeError(`Expected a 26-character ULID, received "${value}".`);
  }

  return ulid;
}

export function ulidPathSegment(value: Ulid): string {
  return encodeURIComponent(value);
}
