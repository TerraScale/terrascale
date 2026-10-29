# Collection access rules

Producer: TerraScale/terrascale@1d823d8534fc5035859a47e263691d0c7cb7b744.
Features/Documents/GetCollectionRulesEndpoint.cs, PutCollectionRulesEndpoint.cs
and CollectionRulesRoute.cs own GET/PUT
/v1/projects/{project}/databases/{database}/collections/{collection}/rules,
getCollectionRules/putCollectionRules, documents:read/write project authority.
End-users are refused with 403. JSON {rules:null} denies every end-user operation;
public grants still require authenticated end-users, never anonymous callers.

PUT takes the policy itself: optional/null owner JSON Pointer, required read and
write policies with owner/public booleans and required roles arrays. Roles are
trusted and case-sensitive; all-false grants with empty arrays deny access.
Engine/CollectionAccessRules.cs and DocumentService.Rules.cs own pointer/role
validation and immutable ownership. PUT is bounded to 16 KiB, with no idempotency
contract or SDK mutation replay. Preserve 400 invalid_rules, 403, 404, 413, 415,
503 and their typed envelopes. No file-bucket authority is substituted.

The producer route fixture is tests/TerraBase.Fast/Platform/ApiReferenceTests.cs.
HTTP fixtures establish request/response/denial/cancellation behavior without
producer binaries. Generated/deployed parity and live own/foreign/end-user
qualification remain pending; source evidence is not live proof.
