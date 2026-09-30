package sdk

import (
	"context"
	"encoding/json"
	"math"
	"net/http"
	"slices"
	"strings"
)

// Policy management uses the instance management credential. These factories
// keep registration separate from originating and introspection credentials.
func (i *TSAuthInstanceClient) OAuthResourcePolicies() *TSAuthOAuthResourcePoliciesClient {
	return &TSAuthOAuthResourcePoliciesClient{i: i}
}

func (i *TSAuthInstanceClient) OAuthClientPolicies() *TSAuthOAuthClientPoliciesClient {
	return &TSAuthOAuthClientPoliciesClient{i: i}
}

type TSAuthOAuthResourcePoliciesClient struct{ i *TSAuthInstanceClient }
type TSAuthOAuthClientPoliciesClient struct{ i *TSAuthInstanceClient }

type CreateTSAuthOAuthResourceRequest struct {
	Audience          string   `json:"audience"`
	AllowedClientIDs  []string `json:"allowed_client_ids"`
	AllowedGrantTypes []string `json:"allowed_grant_types"`
	AllowedScopes     []string `json:"allowed_scopes"`
}

type UpdateTSAuthOAuthResourceRequest struct {
	ExpectedAuthEpoch uint64    `json:"expected_auth_epoch"`
	AllowedClientIDs  *[]string `json:"allowed_client_ids,omitempty"`
	AllowedGrantTypes *[]string `json:"allowed_grant_types,omitempty"`
	AllowedScopes     *[]string `json:"allowed_scopes,omitempty"`
	Enabled           *bool     `json:"enabled,omitempty"`
	RotateSecret      bool      `json:"rotate_secret,omitzero"`
}

type TSAuthOAuthResource struct {
	ID                      string   `json:"id"`
	Audience                string   `json:"audience"`
	AllowedClientIDs        []string `json:"allowed_client_ids"`
	AllowedGrantTypes       []string `json:"allowed_grant_types"`
	AllowedScopes           []string `json:"allowed_scopes"`
	IntrospectionClientID   string   `json:"introspection_client_id"`
	TokenEndpointAuthMethod string   `json:"token_endpoint_auth_method"`
	AuthEpoch               uint64   `json:"auth_epoch"`
	Enabled                 bool     `json:"enabled"`
	CreatedAt               int64    `json:"created_at"`
	UpdatedAt               int64    `json:"updated_at"`
}

type TSAuthOAuthResourceResult struct {
	Resource            TSAuthOAuthResource `json:"resource"`
	IntrospectionSecret *string             `json:"introspection_secret"`
	requireSecret       bool
}

func (TSAuthOAuthResourceResult) String() string {
	return "TSAuthOAuthResourceResult{secret:[REDACTED]}"
}

var tsAuthResourcePolicyFields = []string{"id", "audience", "allowed_client_ids", "allowed_grant_types", "allowed_scopes", "introspection_client_id", "token_endpoint_auth_method", "auth_epoch", "enabled", "created_at", "updated_at"}

func tsAuthDecodeResourcePolicy(body []byte, out *TSAuthOAuthResource) error {
	if _, err := tsAuthPolicyObject(http.StatusOK, body, tsAuthResourcePolicyFields, nil); err != nil {
		return err
	}
	if err := json.Unmarshal(body, out); err != nil || out.ID == "" || out.IntrospectionClientID == "" || out.AuthEpoch == 0 || out.TokenEndpointAuthMethod != string(TSAuthClientSecretBasic) {
		return oidcError("invalid_resource_policy", "response", false, nil)
	}
	if err := tsAuthResourceAudience(out.Audience); err != nil {
		return err
	}
	return tsAuthPolicy(nil, out.AllowedClientIDs, out.AllowedGrantTypes, out.AllowedScopes, false)
}

func (r *TSAuthOAuthResourceResult) decodeHTTPResponse(status int, body []byte) error {
	object, err := tsAuthPolicyObject(status, body, []string{"resource"}, []string{"introspection_secret"})
	if err != nil || len(object) != 2 {
		return oidcError("invalid_resource_policy", "response", false, nil)
	}
	if err := tsAuthDecodeResourcePolicy(object["resource"], &r.Resource); err != nil {
		return err
	}
	if err := json.Unmarshal(object["introspection_secret"], &r.IntrospectionSecret); err != nil || (r.requireSecret && (r.IntrospectionSecret == nil || *r.IntrospectionSecret == "")) || (!r.requireSecret && r.IntrospectionSecret != nil) {
		return oidcError("invalid_resource_secret_response", "response", false, nil)
	}
	return nil
}

func (o *TSAuthOAuthResourcePoliciesClient) Create(ctx context.Context, request CreateTSAuthOAuthResourceRequest) (*TSAuthOAuthResourceResult, error) {
	if err := tsAuthResourceAudience(request.Audience); err != nil {
		return nil, err
	}
	if request.AllowedClientIDs == nil || request.AllowedGrantTypes == nil || request.AllowedScopes == nil {
		return nil, oidcError("explicit_resource_policy_required", "registration", false, nil)
	}
	if err := tsAuthPolicy(nil, request.AllowedClientIDs, request.AllowedGrantTypes, request.AllowedScopes, false); err != nil {
		return nil, err
	}
	result := TSAuthOAuthResourceResult{requireSecret: true}
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("oauth-resources"), nil, request, &result, "tsauth.oauth_resources.create"); err != nil {
		return nil, err
	}
	if result.Resource.Audience != request.Audience {
		return nil, oidcError("resource_binding_mismatch", "registration", true, nil)
	}
	return &result, nil
}

type tsAuthResourcePolicyList struct{ resources []TSAuthOAuthResource }

func (l *tsAuthResourcePolicyList) decodeHTTPResponse(status int, body []byte) error {
	object, err := tsAuthPolicyObject(status, body, []string{"resources"}, nil)
	if err != nil {
		return err
	}
	var rows []json.RawMessage
	if err := json.Unmarshal(object["resources"], &rows); err != nil || rows == nil {
		return oidcError("invalid_resource_policy", "response", false, nil)
	}
	for _, row := range rows {
		var resource TSAuthOAuthResource
		if err := tsAuthDecodeResourcePolicy(row, &resource); err != nil {
			return err
		}
		l.resources = append(l.resources, resource)
	}
	return nil
}

func (o *TSAuthOAuthResourcePoliciesClient) List(ctx context.Context) ([]TSAuthOAuthResource, error) {
	var result tsAuthResourcePolicyList
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("oauth-resources"), nil, nil, &result, "tsauth.oauth_resources.list"); err != nil {
		return nil, err
	}
	return result.resources, nil
}

func (o *TSAuthOAuthResourcePoliciesClient) Update(ctx context.Context, id string, request UpdateTSAuthOAuthResourceRequest) (*TSAuthOAuthResourceResult, error) {
	if requireID("id", id) != nil || request.ExpectedAuthEpoch == 0 {
		return nil, oidcError("invalid_policy_update", "update", false, nil)
	}
	if err := tsAuthPolicyPatch(nil, request.AllowedClientIDs, request.AllowedGrantTypes, request.AllowedScopes); err != nil {
		return nil, err
	}
	result := TSAuthOAuthResourceResult{requireSecret: request.RotateSecret}
	if err := o.i.tsAuthDo(ctx, http.MethodPatch, o.i.v1("oauth-resources", id), nil, request, &result, "tsauth.oauth_resources.update"); err != nil {
		return nil, err
	}
	if result.Resource.ID != id || request.ExpectedAuthEpoch == math.MaxUint64 || result.Resource.AuthEpoch != request.ExpectedAuthEpoch+1 {
		return nil, oidcError("invalid_policy_update_response", "update", true, nil)
	}
	return &result, nil
}

// Disable retains the immutable audience and introspection identity reservation.
func (o *TSAuthOAuthResourcePoliciesClient) Disable(ctx context.Context, id string) error {
	if err := requireID("id", id); err != nil {
		return err
	}
	return o.i.tsAuthDo(ctx, http.MethodDelete, o.i.v1("oauth-resources", id), nil, nil, nil, "tsauth.oauth_resources.disable")
}

type CreateTSAuthOAuthPolicyClientRequest struct {
	CreateTSAuthOAuthClientRequest
	AllowedResourceAudiences []string `json:"allowed_resource_audiences"`
	AllowedGrantTypes        []string `json:"allowed_grant_types"`
	AllowedScopes            []string `json:"allowed_scopes"`
}

type UpdateTSAuthOAuthPolicyClientRequest struct {
	Name                     *string   `json:"name,omitempty"`
	RedirectURIs             *[]string `json:"redirect_uris,omitempty"`
	PostLogoutRedirectURIs   *[]string `json:"post_logout_redirect_uris,omitempty"`
	AllowedOrigins           *[]string `json:"allowed_origins,omitempty"`
	AllowAnonymous           *bool     `json:"allow_anonymous,omitempty"`
	AllowedResourceAudiences *[]string `json:"allowed_resource_audiences,omitempty"`
	AllowedGrantTypes        *[]string `json:"allowed_grant_types,omitempty"`
	AllowedScopes            *[]string `json:"allowed_scopes,omitempty"`
	ExpectedAuthEpoch        uint64    `json:"expected_auth_epoch"`
	Enabled                  *bool     `json:"enabled,omitempty"`
	RotateSecret             bool      `json:"rotate_secret,omitzero"`
}

type TSAuthOAuthPolicyClient struct {
	TSAuthOAuthClient
	AllowedResourceAudiences []string `json:"allowed_resource_audiences"`
	AllowedGrantTypes        []string `json:"allowed_grant_types"`
	AllowedScopes            []string `json:"allowed_scopes"`
	AuthEpoch                uint64   `json:"auth_epoch"`
	Enabled                  bool     `json:"enabled"`
}

type TSAuthOAuthPolicyClientResult struct {
	Client                 TSAuthOAuthPolicyClient `json:"client"`
	ClientSecret           *string                 `json:"client_secret"`
	wrapper, requireSecret bool
}

func (TSAuthOAuthPolicyClientResult) String() string {
	return "TSAuthOAuthPolicyClientResult{secret:[REDACTED]}"
}

var tsAuthClientPolicyFields = []string{"id", "client_id", "name", "redirect_uris", "token_endpoint_auth_method", "public", "allow_anonymous", "allowed_origins", "post_logout_redirect_uris", "created_at", "allowed_resource_audiences", "allowed_scopes", "allowed_grant_types", "auth_epoch", "enabled"}

func tsAuthDecodeClientPolicy(body []byte, out *TSAuthOAuthPolicyClient) error {
	if _, err := tsAuthPolicyObject(http.StatusOK, body, tsAuthClientPolicyFields, nil); err != nil {
		return err
	}
	if err := json.Unmarshal(body, out); err != nil || out.ID == "" || out.ClientID == "" || out.AuthEpoch == 0 || (out.TokenEndpointAuthMethod != string(TSAuthClientSecretBasic) && out.TokenEndpointAuthMethod != string(TSAuthClientSecretPost) && out.TokenEndpointAuthMethod != string(TSAuthClientAuthenticationNone)) || out.Public != (out.TokenEndpointAuthMethod == string(TSAuthClientAuthenticationNone)) {
		return oidcError("invalid_client_policy", "response", false, nil)
	}
	if err := validateTSAuthOAuthRegistration(out.RedirectURIs, out.PostLogoutRedirectURIs, out.AllowedOrigins); err != nil {
		return err
	}
	return tsAuthPolicy(out.AllowedResourceAudiences, nil, out.AllowedGrantTypes, out.AllowedScopes, out.Public)
}

func (r *TSAuthOAuthPolicyClientResult) decodeHTTPResponse(status int, body []byte) error {
	if !r.wrapper {
		if status != http.StatusOK {
			return oidcError("invalid_client_policy", "response", false, nil)
		}
		return tsAuthDecodeClientPolicy(body, &r.Client)
	}
	object, err := tsAuthPolicyObject(status, body, []string{"client"}, []string{"client_secret"})
	if err != nil || len(object) != 2 {
		return oidcError("invalid_client_policy", "response", false, nil)
	}
	if err := tsAuthDecodeClientPolicy(object["client"], &r.Client); err != nil {
		return err
	}
	if err := json.Unmarshal(object["client_secret"], &r.ClientSecret); err != nil || (r.requireSecret && (r.ClientSecret == nil || *r.ClientSecret == "")) || (!r.requireSecret && r.ClientSecret != nil) {
		return oidcError("invalid_client_secret_response", "response", false, nil)
	}
	return nil
}

func (o *TSAuthOAuthClientPoliciesClient) Create(ctx context.Context, request CreateTSAuthOAuthPolicyClientRequest) (*TSAuthOAuthPolicyClientResult, error) {
	method := request.TokenEndpointAuthMethod
	public := request.Public != nil && *request.Public || method == string(TSAuthClientAuthenticationNone)
	if strings.TrimSpace(request.Name) == "" || (method != string(TSAuthClientSecretBasic) && method != string(TSAuthClientSecretPost) && method != string(TSAuthClientAuthenticationNone)) || (public && method != string(TSAuthClientAuthenticationNone)) || request.AllowedResourceAudiences == nil || request.AllowedGrantTypes == nil || request.AllowedScopes == nil {
		return nil, oidcError("explicit_client_policy_required", "registration", false, nil)
	}
	if err := validateTSAuthOAuthRegistration(request.RedirectURIs, request.PostLogoutRedirectURIs, request.AllowedOrigins); err != nil {
		return nil, err
	}
	if err := tsAuthPolicy(request.AllowedResourceAudiences, nil, request.AllowedGrantTypes, request.AllowedScopes, public); err != nil {
		return nil, err
	}
	result := TSAuthOAuthPolicyClientResult{wrapper: true, requireSecret: !public}
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("oauth-clients"), nil, request, &result, "tsauth.oauth_client_policies.create"); err != nil {
		return nil, err
	}
	return &result, nil
}

type tsAuthClientPolicyList struct{ clients []TSAuthOAuthPolicyClient }

func (l *tsAuthClientPolicyList) decodeHTTPResponse(status int, body []byte) error {
	object, err := tsAuthPolicyObject(status, body, []string{"clients"}, nil)
	if err != nil {
		return err
	}
	var rows []json.RawMessage
	if err := json.Unmarshal(object["clients"], &rows); err != nil || rows == nil {
		return oidcError("invalid_client_policy", "response", false, nil)
	}
	for _, row := range rows {
		var client TSAuthOAuthPolicyClient
		if err := tsAuthDecodeClientPolicy(row, &client); err != nil {
			return err
		}
		l.clients = append(l.clients, client)
	}
	return nil
}

func (o *TSAuthOAuthClientPoliciesClient) List(ctx context.Context) ([]TSAuthOAuthPolicyClient, error) {
	var result tsAuthClientPolicyList
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("oauth-clients"), nil, nil, &result, "tsauth.oauth_client_policies.list"); err != nil {
		return nil, err
	}
	return result.clients, nil
}

// Nonrotating PATCH returns the bare client; rotation returns a client/secret
// wrapper. The decoder requires the branch selected by this request.
func (o *TSAuthOAuthClientPoliciesClient) Update(ctx context.Context, id string, request UpdateTSAuthOAuthPolicyClientRequest) (*TSAuthOAuthPolicyClientResult, error) {
	if requireID("id", id) != nil || request.ExpectedAuthEpoch == 0 || request.Name != nil && strings.TrimSpace(*request.Name) == "" {
		return nil, oidcError("invalid_policy_update", "update", false, nil)
	}
	var redirects, logout, origins []string
	if request.RedirectURIs != nil {
		redirects = *request.RedirectURIs
	}
	if request.PostLogoutRedirectURIs != nil {
		logout = *request.PostLogoutRedirectURIs
	}
	if request.AllowedOrigins != nil {
		origins = *request.AllowedOrigins
	}
	if err := validateTSAuthOAuthPatch(redirects, logout, origins, request.RedirectURIs != nil); err != nil {
		return nil, err
	}
	if err := tsAuthPolicyPatch(request.AllowedResourceAudiences, nil, request.AllowedGrantTypes, request.AllowedScopes); err != nil {
		return nil, err
	}
	result := TSAuthOAuthPolicyClientResult{wrapper: request.RotateSecret, requireSecret: request.RotateSecret}
	if err := o.i.tsAuthDo(ctx, http.MethodPatch, o.i.v1("oauth-clients", id), nil, request, &result, "tsauth.oauth_client_policies.update"); err != nil {
		return nil, err
	}
	if result.Client.ID != id || request.ExpectedAuthEpoch == math.MaxUint64 || result.Client.AuthEpoch != request.ExpectedAuthEpoch+1 {
		return nil, oidcError("invalid_policy_update_response", "update", true, nil)
	}
	return &result, nil
}

func (o *TSAuthOAuthClientPoliciesClient) Disable(ctx context.Context, id string) error {
	return o.i.OAuthClients.Delete(ctx, id)
}

func tsAuthPolicy(audiences, clients, grants, scopes []string, public bool) error {
	for _, values := range [][]string{audiences, clients, grants, scopes} {
		if len(values) > 100 {
			return oidcError("invalid_oauth_policy", "policy", false, nil)
		}
		encoded, err := json.Marshal(values)
		if err != nil || len(encoded) > 32768 {
			return oidcError("invalid_oauth_policy", "policy", false, nil)
		}
		seen := make(map[string]bool, len(values))
		for _, value := range values {
			if value == "" || seen[value] {
				return oidcError("invalid_oauth_policy", "policy", false, nil)
			}
			seen[value] = true
		}
	}
	for _, audience := range audiences {
		if err := tsAuthResourceAudience(audience); err != nil {
			return err
		}
	}
	for _, grant := range grants {
		if !slices.Contains([]string{"authorization_code", "refresh_token", "client_credentials", TSAuthDeviceCodeGrant}, grant) || public && grant == "client_credentials" {
			return oidcError("invalid_oauth_policy", "policy", false, nil)
		}
	}
	return tsAuthResourceScopes(scopes)
}

func tsAuthPolicyPatch(audiences, clients, grants, scopes *[]string) error {
	var a, c, g, s []string
	for _, value := range []*[]string{audiences, clients, grants, scopes} {
		if value != nil && *value == nil {
			return oidcError("null_policy_vector", "policy", false, nil)
		}
	}
	if audiences != nil {
		a = *audiences
	}
	if clients != nil {
		c = *clients
	}
	if grants != nil {
		g = *grants
	}
	if scopes != nil {
		s = *scopes
	}
	return tsAuthPolicy(a, c, g, s, false)
}

func (r TSAuthOAuthResourceResult) GoString() string     { return r.String() }
func (r TSAuthOAuthPolicyClientResult) GoString() string { return r.String() }
