import { expectTypeOf, test } from "vitest";
/**
 * @import {
 *   DatabaseValue,
 *   DatabaseScope,
 *   DatabasePoint,
 *   DatabaseTransactionStatus,
 * } from "./database-types.js"
 */

/** Type-only placeholder: `expectTypeOf` is evaluated statically here. */
const typeOnly = /** @type {never} */ (undefined);

test("public native value/scope/status fidelity is statically preserved", () => {
  expectTypeOf(
    /** @type {DatabaseScope["shard"]} */ (typeOnly),
  ).toEqualTypeOf(/** @type {bigint} */ (typeOnly));
  expectTypeOf(
    /** @type {Extract<DatabaseValue, { type: "i64" }>["value"]} */ (typeOnly),
  ).toEqualTypeOf(/** @type {bigint} */ (typeOnly));
  expectTypeOf(
    /** @type {Extract<DatabaseValue, { type: "bytes" }>["value"]} */ (
      typeOnly
    ),
  ).toEqualTypeOf(/** @type {Uint8Array} */ (typeOnly));
  expectTypeOf(
    /** @type {DatabasePoint["state_version"]} */ (typeOnly),
  ).toEqualTypeOf(/** @type {string | null} */ (typeOnly));
  expectTypeOf(
    /**
     * @type {Extract<
     *   DatabaseTransactionStatus,
     *   { state: "rejected" }
     * >["outcome"]["error"]["code"]}
     */ (typeOnly),
  ).toEqualTypeOf(/** @type {"precondition_failed"} */ (typeOnly));
});
