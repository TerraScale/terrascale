import { answersRegion, judge } from "../../status-site/src/domain/assertions.ts";
import { consensus } from "../../status-site/src/domain/consensus.ts";
import { overallStatus, reported } from "../../status-site/src/domain/status.ts";

const spec = { expectStatus: "200", keyword: null, assertions: [], extract: {}, indirect: false };
const unreachable = judge(spec, { status: null, error: "Unreachable" });
if (unreachable.outcome !== "unknown") throw new Error(`unreachable outcome ${unreachable.outcome}`);
const httpFailure = judge(spec, { status: 500, error: null, body: "" });
if (httpFailure.outcome !== "fail") throw new Error(`http failure outcome ${httpFailure.outcome}`);

const revoked = reported(["operational", "none"]);
if (revoked !== "none") throw new Error(`revoked rollup ${revoked}`);
const now = Date.now();
if (overallStatus(["operational", revoked], [], now) !== "unknown") throw new Error("revoked page");
if (reported(["major_outage", "none"]) !== "major_outage") throw new Error("outage hidden");

const round = consensus([]);
if (round.state !== "none") throw new Error(`unreachable round ${round.state}`);
if (overallStatus([round.state], [], now) !== "unknown") throw new Error("unreachable page");
if (answersRegion('{"status":"ok","region":"gru"}', "sjc") !== false) throw new Error("other region");
if (answersRegion('{"status":"ok","region":"sjc"}', "sjc") !== true) throw new Error("same region");

console.log("revoked credential: unknown");
console.log("unreachable component: unknown");
console.log("partial measurement: unknown");
