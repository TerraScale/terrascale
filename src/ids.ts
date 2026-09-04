declare const terraScaleBrand: unique symbol;

export type Brand<TValue, TBrand extends string> = TValue & {
  /**
   * The marker is optional so generated wire-model strings remain source
   * compatible while values returned by the runtime helpers carry the brand.
   */
  readonly [terraScaleBrand]?: TBrand;
};

export type Ulid = Brand<string, "Ulid">;

export type OrganizationId = Ulid;
export type ProjectId = Ulid;
export type DatabaseId = Ulid;
export type UserId = Ulid;
export type InvitationId = Ulid;
export type BackupId = Ulid;
export type BackupPolicyId = Ulid;
export type NotificationId = Ulid;
export type AuditEventId = Ulid;
export type RestoreId = Ulid;

const ulidPattern = /^[0-9A-HJKMNP-TV-Z]{26}$/u;

/** Return true when the value is a well-formed ULID literal. */
export function isUlid(value: string): value is Ulid {
  return ulidPattern.test(value);
}

/** Return the value branded as a Ulid when it is a well-formed ULID literal. */
export function tryUlid(value: string): Ulid | undefined {
  return ulidPattern.test(value) ? (value as Ulid) : undefined;
}

/** Assert a well-formed ULID literal and return it branded as a Ulid. */
export function toUlid(value: string): Ulid {
  const ulid = tryUlid(value);
  if (ulid === undefined) throw new TypeError(`Not a valid ULID: ${value}`);
  return ulid;
}
