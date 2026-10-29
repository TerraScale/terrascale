import type { TsAuthOidcTokenRequest } from "@terrascale/sdk/ts-auth";
export const project: TsAuthOidcTokenRequest = { grant_type: "client_credentials", project: "01ARZ3NDEKTSV4RRFFQ69G5FA1" };
export const subset: TsAuthOidcTokenRequest = { ...project, scope: "documents:read" };
export const resource: TsAuthOidcTokenRequest = { grant_type: "client_credentials", resource: "https://api.example.test", scope: "documents:read" };
// @ts-expect-error project and resource select different contracts, including through a variable
export const mixed: TsAuthOidcTokenRequest = { ...resource, project: "01ARZ3NDEKTSV4RRFFQ69G5FA1" };
// @ts-expect-error explicit project is required without a resource
export const unscoped: TsAuthOidcTokenRequest = { grant_type: "client_credentials", scope: "documents:read" };
// @ts-expect-error resource grants retain their required scope
export const noScope: TsAuthOidcTokenRequest = { grant_type: "client_credentials", resource: "https://api.example.test" };
// @ts-expect-error project is a canonical ULID string, validated at runtime
export const numericProject: TsAuthOidcTokenRequest = { grant_type: "client_credentials", project: 123 };
