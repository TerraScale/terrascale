import { requireId } from "./ids.js";
/** @import { TsAuthHttp } from "./http.js" */
/** @import { TerraScaleResult } from "./result.js" */
/**
 * @typedef {{
 * readonly id: string;
 * readonly name: string;
 * readonly slug: string;
 * readonly image_url: string | null;
 * readonly public_metadata: Readonly<Record<string, unknown>>;
 * readonly private_metadata: Readonly<Record<string, unknown>>;
 * readonly max_members: number | null;
 * readonly domain_join: string;
 * readonly created_at: string;
 * }} TsAuthOrganization
 */
/**
 * @typedef {{
 * readonly name: string;
 * readonly slug: string;
 * readonly admin_user_id: string | null;
 * readonly image_url: string | null;
 * readonly public_metadata: Readonly<Record<string, unknown>> | null;
 * readonly private_metadata: Readonly<Record<string, unknown>> | null;
 * readonly max_members: number | null;
 * readonly domain_join: string;
 * }} TsAuthOrganizationRequest
 */
/**
 * @typedef {{
 * readonly project_id: string;
 * readonly organization_id: string;
 * readonly user_id: string;
 * readonly role: string;
 * readonly created_at: string;
 * }} TsAuthOrganizationMembership
 */
/**
 * @typedef {{
 * readonly user_id: string | null;
 * readonly role: string;
 * }} TsAuthOrganizationMembershipRequest
 */
/**
 * @typedef {{
 * readonly id: string;
 * readonly email: string;
 * readonly role: string;
 * readonly status: string;
 * readonly created_at: string;
 * readonly expires_at: string;
 * }} TsAuthOrganizationInvitation
 */
/**
 * @typedef {{
 * readonly email: string;
 * readonly role: string;
 * }} TsAuthOrganizationInvitationRequest
 */
/**
 * @typedef {{
 * readonly domain: string;
 * readonly record_name: string;
 * readonly record_value: string;
 * readonly verified_at: string | null;
 * }} TsAuthOrganizationDomain
 */
/**
 * @typedef {{
 * readonly domain: string;
 * }} TsAuthOrganizationDomainRequest
 */
/** @typedef {{readonly items: readonly TsAuthOrganization[];readonly next: string | null;}} TsAuthOrganizationPage */
/** @typedef {{readonly items: readonly TsAuthOrganizationMembership[];readonly next: string | null;}} TsAuthOrganizationMembershipPage */
/** @typedef {{readonly items: readonly TsAuthOrganizationInvitation[];readonly next: string | null;}} TsAuthOrganizationInvitationPage */
/** @typedef {{readonly items: readonly TsAuthOrganizationDomain[];}} TsAuthOrganizationDomainPage */
export class TsAuthOrganizationsClient {
  /** @type {TsAuthHttp} */
  #http;
  /** @param {TsAuthHttp} http */
  constructor(http) { this.#http = http; }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {TsAuthOrganizationRequest} request
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganization>>}
   */
  createOrganization(credential, project, request, options = {}) {
    return this.#http.request({
      method: "POST", path: `v1/${requireId(project, "project")}/organizations`,
      routeName: "tsauth.projects.create_organization", projectAccessToken: credential,
      body: request,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {number} [limit=20]
   * @param {string | null} [after=null]
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationPage>>}
   */
  listOrganizations(credential, project, limit = 20, after = null, options = {}) {
    return this.#http.request({
      method: "GET", path: `v1/${requireId(project, "project")}/organizations?limit=${limit}${after === null ? "" : "&after=" + encodeURIComponent(after)}`,
      routeName: "tsauth.projects.list_organizations", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganization>>}
   */
  getOrganization(credential, project, org, options = {}) {
    return this.#http.request({
      method: "GET", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}`,
      routeName: "tsauth.projects.get_organization", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {TsAuthOrganizationRequest} request
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganization>>}
   */
  updateOrganization(credential, project, org, request, options = {}) {
    return this.#http.request({
      method: "PUT", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}`,
      routeName: "tsauth.projects.update_organization", projectAccessToken: credential,
      body: request,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteOrganization(credential, project, org, options = {}) {
    return this.#http.request({
      method: "DELETE", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}`,
      routeName: "tsauth.projects.delete_organization", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {TsAuthOrganizationMembershipRequest} request
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationMembership>>}
   */
  addOrganizationMember(credential, project, org, request, options = {}) {
    return this.#http.request({
      method: "POST", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/memberships`,
      routeName: "tsauth.projects.add_organization_member", projectAccessToken: credential,
      body: request,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {number} [limit=20]
   * @param {string | null} [after=null]
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationMembershipPage>>}
   */
  listOrganizationMembers(credential, project, org, limit = 20, after = null, options = {}) {
    return this.#http.request({
      method: "GET", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/memberships?limit=${limit}${after === null ? "" : "&after=" + encodeURIComponent(after)}`,
      routeName: "tsauth.projects.list_organization_members", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {string} user
   * @param {TsAuthOrganizationMembershipRequest} request
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationMembership>>}
   */
  updateOrganizationMember(credential, project, org, user, request, options = {}) {
    return this.#http.request({
      method: "PUT", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/memberships/${encodeURIComponent(user)}`,
      routeName: "tsauth.projects.update_organization_member", projectAccessToken: credential,
      body: request,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {string} user
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteOrganizationMember(credential, project, org, user, options = {}) {
    return this.#http.request({
      method: "DELETE", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/memberships/${encodeURIComponent(user)}`,
      routeName: "tsauth.projects.delete_organization_member", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {TsAuthOrganizationInvitationRequest} request
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationInvitation>>}
   */
  createOrganizationInvitation(credential, project, org, request, options = {}) {
    return this.#http.request({
      method: "POST", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/invitations`,
      routeName: "tsauth.projects.create_organization_invitation", projectAccessToken: credential,
      body: request,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {number} [limit=20]
   * @param {string | null} [after=null]
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationInvitationPage>>}
   */
  listOrganizationInvitations(credential, project, org, limit = 20, after = null, options = {}) {
    return this.#http.request({
      method: "GET", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/invitations?limit=${limit}${after === null ? "" : "&after=" + encodeURIComponent(after)}`,
      routeName: "tsauth.projects.list_organization_invitations", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {string} invitation
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  revokeOrganizationInvitation(credential, project, org, invitation, options = {}) {
    return this.#http.request({
      method: "DELETE", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/invitations/${encodeURIComponent(invitation)}`,
      routeName: "tsauth.projects.revoke_organization_invitation", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {TsAuthOrganizationDomainRequest} request
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationDomain>>}
   */
  addOrganizationDomain(credential, project, org, request, options = {}) {
    return this.#http.request({
      method: "POST", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/domains`,
      routeName: "tsauth.projects.add_organization_domain", projectAccessToken: credential,
      body: request,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationDomainPage>>}
   */
  listOrganizationDomains(credential, project, org, options = {}) {
    return this.#http.request({
      method: "GET", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/domains`,
      routeName: "tsauth.projects.list_organization_domains", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {string} domain
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<TsAuthOrganizationDomain>>}
   */
  verifyOrganizationDomain(credential, project, org, domain, options = {}) {
    return this.#http.request({
      method: "POST", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/domains/${encodeURIComponent(domain)}/verify`,
      routeName: "tsauth.projects.verify_organization_domain", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
  /**
   * @param {string} credential
   * @param {string} project
   * @param {string} org
   * @param {string} domain
   * @param {{signal?: AbortSignal | undefined}} [options={}]
   * @returns {Promise<TerraScaleResult<void>>}
   */
  deleteOrganizationDomain(credential, project, org, domain, options = {}) {
    return this.#http.request({
      method: "DELETE", path: `v1/${requireId(project, "project")}/organizations/${encodeURIComponent(org)}/domains/${encodeURIComponent(domain)}`,
      routeName: "tsauth.projects.delete_organization_domain", projectAccessToken: credential,
      ...(options.signal === undefined ? {} : { signal: options.signal }),
    });
  }
}
