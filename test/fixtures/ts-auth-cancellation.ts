import type { TsAuthClient, TsAuthOidcResourceCredentials, TsAuthRequestOptions } from "@terrascale/sdk/ts-auth";
declare const client: TsAuthClient;
declare const credentials: TsAuthOidcResourceCredentials;
const options: TsAuthRequestOptions = { signal: new AbortController().signal };
client.users.get("01ARZ3NDEKTSV4RRFFQ69G5FA1", options);
client.users.list({ limit: 1, ...options });
client.users.listAll({ pageSize: 1, ...options });
client.oidc.discovery(options);
client.oidc.jwks(options);
client.oidc.deviceAuthorization("client", "secret", undefined, options);
client.oidc.token("client", "secret", { grant_type: "client_credentials", project: "01ARZ3NDEKTSV4RRFFQ69G5FA1" }, options);
client.oidc.introspect("client", "secret", "token", options);
client.oidc.introspectResource(credentials, "token", undefined, options);
client.oidc.revoke("client", "secret", "token", options);
client.oidc.userinfo("token", options);
// @ts-expect-error cancellation is an AbortSignal
client.oidc.discovery({ signal: "abort" });
// @ts-expect-error named request options expose no custom headers
client.users.get("01ARZ3NDEKTSV4RRFFQ69G5FA1", { headers: { authorization: "custom" } });
