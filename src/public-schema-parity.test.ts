import { expectTypeOf, test } from "vitest";
import type {
  DatabaseValue,
  DatabaseScope,
  DatabasePoint,
  DatabaseTransactionStatus,
} from "./index.js";
test("public native value/scope/status fidelity is statically preserved", () => {
  expectTypeOf<DatabaseScope["shard"]>().toEqualTypeOf<bigint>();
  expectTypeOf<
    Extract<DatabaseValue, { type: "i64" }>["value"]
  >().toEqualTypeOf<bigint>();
  expectTypeOf<
    Extract<DatabaseValue, { type: "bytes" }>["value"]
  >().toEqualTypeOf<Uint8Array>();
  expectTypeOf<DatabasePoint["state_version"]>().toEqualTypeOf<string | null>();
  expectTypeOf<
    Extract<
      DatabaseTransactionStatus,
      { state: "rejected" }
    >["outcome"]["error"]["code"]
  >().toEqualTypeOf<"precondition_failed">();
});
