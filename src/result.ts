export type TerraScaleErrorKind =
  | "authentication"
  | "authorization"
  | "conflict"
  | "domain"
  | "invalid-response"
  | "not-found"
  | "rate-limit"
  | "server"
  | "transport"
  | "validation";

export type TerraScaleValidationIssue = {
  readonly path?: string;
  readonly message: string;
  readonly code?: string;
};

export type TerraScaleError = {
  readonly kind: TerraScaleErrorKind;
  readonly code: string;
  readonly message: string;
  readonly status?: number | undefined;
  readonly target?: string | undefined;
  readonly requestId?: string | undefined;
  readonly validation?: readonly TerraScaleValidationIssue[] | undefined;
  readonly details?: unknown | undefined;
};

export type TerraScaleSuccess<TValue> = {
  readonly ok: true;
  readonly routeName: string;
  readonly status: number;
  readonly headers: Headers;
  readonly value: TValue;
};

export type TerraScaleFailure = {
  readonly ok: false;
  readonly routeName: string;
  readonly status?: number | undefined;
  readonly headers?: Headers | undefined;
  readonly error: TerraScaleError;
  readonly body?: unknown | undefined;
};

export type TerraScaleResult<TValue> = TerraScaleFailure | TerraScaleSuccess<TValue>;

export function ok<TValue>(
  routeName: string,
  status: number,
  headers: Headers,
  value: TValue,
): TerraScaleSuccess<TValue> {
  return {
    ok: true,
    routeName,
    status,
    headers,
    value,
  };
}

export function err(
  routeName: string,
  error: TerraScaleError,
  status?: number,
  headers?: Headers,
  body?: unknown,
): TerraScaleFailure {
  return {
    ok: false,
    routeName,
    status,
    headers,
    error,
    body,
  };
}

export function isSuccess<TValue>(
  result: TerraScaleResult<TValue>,
): result is TerraScaleSuccess<TValue> {
  return result.ok;
}

export function isFailure<TValue>(result: TerraScaleResult<TValue>): result is TerraScaleFailure {
  return !result.ok;
}
