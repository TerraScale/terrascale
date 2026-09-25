package authentication

import (
	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/ui/icons"
)

// Field types. Most map to an HTML input type; the rest render as a select
// of instance records, a role picker, a select, or a checkbox group.
const (
	typeText         = "text"
	typeEmail        = "email"
	typeURL          = "url"
	typeNumber       = "number"
	typeTextarea     = "textarea"
	typeSecret       = "password"
	typeOrganization = "organization"
	typeUser         = "user"
	typeRole         = "role"
	typeSelect       = "select"
	typeChoices      = "choices"
)

type option struct {
	Value string
	Label string
}

type field struct {
	Key         string
	Label       string
	Type        string
	Hint        string
	Placeholder string
	Required    bool
	// Immutable fields are fixed at creation, mirroring ts-auth update
	// contracts (an organization slug or a membership's user cannot change).
	Immutable bool
	// Unique fields are claimed once per instance, like ts-auth's claim
	// documents for emails, usernames, slugs, and template names.
	Unique  bool
	Options []option
}

type section struct {
	Key         string
	Title       string
	Group       string
	Description string
	// Noun is the singular record name used in buttons and messages.
	Noun     string
	IDPrefix string
	Icon     func(int) templ.Component
	Tone     string
	Fields   []field
	Editable bool
	// Revocable records are kept with a Revoked status instead of deleted.
	Revocable   bool
	EmptyDetail string
}

// Sections that list instance records rather than settings.
func (s section) lists() bool { return len(s.Fields) > 0 }

var (
	roleOptions = []option{{"admin", "Admin"}, {"member", "Member"}}

	// managementScopes are ts-auth's management route families; a key needs
	// the family of a /v1/{slug}/{family} route or the "*" wildcard.
	managementScopes = []option{
		{"*", "All routes"}, {"users", "Users"}, {"sessions", "Sessions"}, {"organizations", "Organizations"},
		{"domains", "Domains"}, {"connections", "Connections"}, {"directories", "Directories"},
		{"oauth-clients", "OAuth clients"}, {"jwt-templates", "JWT templates"}, {"webhooks", "Webhooks"},
		{"audit-events", "Audit events"}, {"api-keys", "API keys"}, {"instance", "Instance"}, {"billing", "Billing"},
	}

	// webhookEvents are the audit event types ts-auth delivers to endpoints.
	webhookEvents = []option{
		{"*", "All events"},
		{"user.created", "user.created"}, {"user.updated", "user.updated"}, {"user.deleted", "user.deleted"},
		{"user.signed_up", "user.signed_up"}, {"user.signed_in", "user.signed_in"}, {"user.signed_out", "user.signed_out"},
		{"user.email_verified", "user.email_verified"}, {"user.password_changed", "user.password_changed"},
		{"user.password_reset", "user.password_reset"}, {"user.impersonated", "user.impersonated"},
		{"organization.created", "organization.created"}, {"organization.deleted", "organization.deleted"},
		{"organization.enrolled", "organization.enrolled"},
		{"membership.created", "membership.created"}, {"membership.updated", "membership.updated"},
		{"membership.deleted", "membership.deleted"},
		{"invitation.created", "invitation.created"}, {"invitation.accepted", "invitation.accepted"},
	}

	clientTypes = []option{
		{"client_secret_basic", "Confidential · client_secret_basic"},
		{"client_secret_post", "Confidential · client_secret_post"},
		{"none", "Public · PKCE only"},
	}
)

// sections follow the order and grouping of core.AuthNavigation.
var sections = []section{
	{Key: "overview", Title: "Overview", Description: "Setup progress and identity totals for this instance.", Icon: icons.LayoutDashboard, Tone: "emerald"},
	{Key: "quickstart", Title: "Quickstart", Description: "Connect an application to this instance with OpenID Connect.", Icon: icons.Zap, Tone: "amber"},
	{
		Key: "users", Title: "Users", Group: "Manage", Noun: "user", IDPrefix: "user", Icon: icons.Users, Tone: "pink", Editable: true,
		Description: "People who can sign in to your application.",
		EmptyDetail: "Users appear here when they sign up on the hosted pages, or when you create them.",
		Fields: []field{
			{Key: "name", Label: "Full name", Type: typeText, Placeholder: "Ada Lovelace"},
			{Key: "email", Label: "Email address", Type: typeEmail, Required: true, Unique: true, Placeholder: "ada@example.com"},
			{Key: "username", Label: "Username", Type: typeText, Unique: true, Placeholder: "ada", Hint: "Optional. Used when username sign-in is enabled."},
		},
	},
	{
		Key: "organizations", Title: "Organizations", Group: "Manage", Noun: "organization", IDPrefix: "org", Icon: icons.Building2, Tone: "indigo", Editable: true,
		Description: "Customer workspaces with their own members, roles, and SSO.",
		EmptyDetail: "Create an organization to group users, send invitations, and scope SSO connections.",
		Fields: []field{
			{Key: "name", Label: "Name", Type: typeText, Required: true, Placeholder: "Acme Inc."},
			{Key: "slug", Label: "Slug", Type: typeText, Required: true, Immutable: true, Unique: true, Placeholder: "acme", Hint: "Lowercase letters, numbers, and dashes. Cannot change later."},
		},
	},
	{
		Key: "memberships", Title: "Memberships", Group: "Manage", Noun: "membership", IDPrefix: "mem", Icon: icons.Network, Tone: "violet", Editable: true,
		Description: "Which users belong to which organization, and with what role.",
		EmptyDetail: "Add a user to an organization to give them a role there.",
		Fields: []field{
			{Key: "organization", Label: "Organization", Type: typeOrganization, Required: true, Immutable: true},
			{Key: "user", Label: "User", Type: typeUser, Required: true, Immutable: true},
			{Key: "role", Label: "Role", Type: typeRole, Required: true, Options: roleOptions, Hint: "admin, member, or any custom role up to 64 characters."},
		},
	},
	{
		Key: "invitations", Title: "Invitations", Group: "Manage", Noun: "invitation", IDPrefix: "inv", Icon: icons.MailPlus, Tone: "sky", Revocable: true,
		Description: "Pending email invitations to join an organization.",
		EmptyDetail: "Invite someone by email; they join the organization when they accept.",
		Fields: []field{
			{Key: "email", Label: "Email address", Type: typeEmail, Required: true, Placeholder: "teammate@example.com"},
			{Key: "organization", Label: "Organization", Type: typeOrganization, Required: true},
			{Key: "role", Label: "Role", Type: typeRole, Required: true, Options: roleOptions},
		},
	},
	{
		Key: "domains", Title: "Enrollment domains", Group: "Manage", Noun: "domain", IDPrefix: "dom", Icon: icons.Globe, Tone: "teal",
		Description: "Users with a verified email on these domains can join the organization.",
		EmptyDetail: "Add a domain such as acme.com so verified users can enroll without an invitation.",
		Fields: []field{
			{Key: "domain", Label: "Domain", Type: typeText, Required: true, Unique: true, Placeholder: "acme.com"},
			{Key: "organization", Label: "Organization", Type: typeOrganization, Required: true},
		},
	},
	{Key: "sign-in", Title: "Sign-in methods", Group: "User & authentication", Description: "How users identify themselves and prove who they are.", Icon: icons.LockKeyhole, Tone: "blue"},
	{
		Key: "connections", Title: "SSO connections", Group: "User & authentication", Noun: "connection", IDPrefix: "conn", Icon: icons.GlobeLock, Tone: "cyan", Editable: true,
		Description: "Social and enterprise identity providers users can sign in with.",
		EmptyDetail: "Add Google, Microsoft, or GitHub for social sign-in, or OIDC and SAML for enterprise SSO.",
		// Provider forms own the connection fields; see providers.
		Fields: []field{{Key: "name", Label: "Name", Type: typeText, Required: true}},
	},
	{Key: "mfa", Title: "Multi-factor", Group: "User & authentication", Description: "Second factors users can enroll and must pass at sign-in.", Icon: icons.ShieldCheck, Tone: "lime"},
	{Key: "sessions", Title: "Sessions", Group: "User & authentication", Description: "How long sign-ins last and how they end.", Icon: icons.Monitor, Tone: "orange"},
	{
		Key: "directories", Title: "Directory sync", Group: "User & authentication", Noun: "directory", IDPrefix: "dir", Icon: icons.Layers, Tone: "fuchsia",
		Description: "SCIM 2.0 directories that provision and deprovision users from an identity provider.",
		EmptyDetail: "Create a directory and give its SCIM endpoint and token to Okta, Entra ID, or another provider.",
		Fields: []field{
			{Key: "organization", Label: "Scope", Type: typeOrganization, Hint: "Leave empty for an instance-wide directory that controls authentication for every linked user."},
		},
	},
	{
		Key: "clients", Title: "OAuth applications", Group: "Developers", Noun: "application", IDPrefix: "client", Icon: icons.Boxes, Tone: "violet",
		Description: "Applications that sign users in through the OAuth 2.1 authorization code flow with PKCE.",
		EmptyDetail: "Register an application to get a client ID for the authorization code flow.",
		Fields: []field{
			{Key: "name", Label: "Application name", Type: typeText, Required: true, Placeholder: "Web app"},
			{Key: "type", Label: "Client authentication", Type: typeSelect, Required: true, Options: clientTypes, Hint: "Public clients run in the browser or on devices and cannot keep a secret."},
			{Key: "redirect_uris", Label: "Redirect URIs", Type: typeTextarea, Required: true, Placeholder: "https://app.example.com/callback", Hint: "One per line. HTTPS, or HTTP on localhost and loopback addresses."},
		},
	},
	{
		Key: "templates", Title: "JWT templates", Group: "Developers", Noun: "template", IDPrefix: "jtp", Icon: icons.FileJson, Tone: "amber", Editable: true,
		Description: "Custom-claim tokens minted on demand for third-party services.",
		EmptyDetail: "Create a template to mint short-lived tokens with the claims another service expects.",
		Fields: []field{
			{Key: "name", Label: "Name", Type: typeText, Required: true, Unique: true, Placeholder: "supabase", Hint: "Up to 48 letters, numbers, dashes, and underscores."},
			{Key: "claims", Label: "Claims", Type: typeTextarea, Required: true, Placeholder: `{"sub": "{{user.id}}", "email": "{{user.email}}"}`},
			{Key: "lifetime_seconds", Label: "Lifetime in seconds", Type: typeNumber, Required: true, Placeholder: "300"},
		},
	},
	{
		Key: "keys", Title: "Management keys", Group: "Developers", Noun: "key", IDPrefix: "key", Icon: icons.KeySquare, Tone: "rose", Revocable: true,
		Description: "Scoped sk_ keys for the ts-auth Management API. This console records keys without issuing secrets.",
		EmptyDetail: "Create a key scoped to the route families your backend calls.",
		Fields: []field{
			{Key: "name", Label: "Key name", Type: typeText, Required: true, Placeholder: "Backend"},
			{Key: "scopes", Label: "Scopes", Type: typeChoices, Required: true, Options: managementScopes, Hint: "A key can call /v1/{slug}/{family} routes for each selected family."},
		},
	},
	{
		Key: "webhooks", Title: "Webhooks", Group: "Developers", Noun: "endpoint", IDPrefix: "wh", Icon: icons.Webhook, Tone: "orange",
		Description: "HMAC-signed event deliveries with retries and a dead-letter queue.",
		EmptyDetail: "Add an HTTPS endpoint to receive user, organization, and membership events.",
		Fields: []field{
			{Key: "url", Label: "Endpoint URL", Type: typeURL, Required: true, Placeholder: "https://api.example.com/webhooks/auth"},
			{Key: "events", Label: "Events", Type: typeChoices, Required: true, Options: webhookEvents},
		},
	},
	{Key: "audit", Title: "Audit log", Group: "Developers", Description: "Authentication changes made in this project.", Icon: icons.ScrollText, Tone: "slate"},
	{Key: "branding", Title: "Branding", Group: "Customization", Description: "Name, logo, and color of the hosted sign-in pages.", Icon: icons.Wand2, Tone: "pink"},
}

func findSection(key string) (section, bool) {
	for _, current := range sections {
		if current.Key == key {
			return current, true
		}
	}
	return section{}, false
}

// provider is one kind of SSO connection. Google and Microsoft are preset
// OpenID Connect issuers; GitHub uses its OAuth app flow; OIDC and SAML are
// enterprise connections that an organization can own.
type provider struct {
	Protocol    string
	Kind        string
	Description string
	Tone        string
	Icon        func(int) templ.Component
	Fields      []field
	// Issuer derives the stored issuer from the submitted values.
	Issuer func(values map[string]string) string
}

var (
	connectionName = field{Key: "name", Label: "Display name", Type: typeText, Required: true, Hint: "Shown on the hosted sign-in button."}
	clientID       = field{Key: "client_id", Label: "Client ID", Type: typeText, Required: true, Immutable: true}
	clientSecret   = field{Key: "client_secret", Label: "Client secret", Type: typeSecret, Required: true, Hint: "Encrypted at rest and never shown again."}
	orgScope       = field{Key: "organization", Label: "Organization", Type: typeOrganization, Immutable: true, Hint: "Optional. Scope the connection to one organization's members."}
)

var providers = []provider{
	{
		Protocol: "Google", Kind: "Social", Tone: "blue", Icon: icons.Globe,
		Description: "Google accounts through Google's OpenID Connect issuer.",
		Fields:      []field{connectionName, clientID, clientSecret},
		Issuer:      func(map[string]string) string { return "https://accounts.google.com" },
	},
	{
		Protocol: "Microsoft", Kind: "Social", Tone: "sky", Icon: icons.Building,
		Description: "Work and school accounts in one Microsoft Entra ID tenant.",
		Fields: []field{
			connectionName,
			{Key: "tenant", Label: "Directory (tenant) ID", Type: typeText, Required: true, Immutable: true, Placeholder: "00000000-0000-0000-0000-000000000000"},
			clientID, clientSecret,
		},
		Issuer: func(values map[string]string) string {
			return "https://login.microsoftonline.com/" + values["tenant"] + "/v2.0"
		},
	},
	{
		Protocol: "GitHub", Kind: "Social", Tone: "slate", Icon: icons.GitBranch,
		Description: "GitHub OAuth apps; only verified email addresses are accepted.",
		Fields:      []field{connectionName, clientID, clientSecret},
		Issuer:      func(map[string]string) string { return "https://github.com" },
	},
	{
		Protocol: "OIDC", Kind: "Enterprise", Tone: "emerald", Icon: icons.ShieldCheck,
		Description: "Any OpenID Connect issuer, discovered from its well-known document.",
		Fields: []field{
			connectionName,
			{Key: "issuer", Label: "Issuer URL", Type: typeURL, Required: true, Immutable: true, Placeholder: "https://login.example.com"},
			clientID, clientSecret,
			{Key: "scopes", Label: "Scopes", Type: typeText, Immutable: true, Placeholder: "openid email profile", Hint: "Space separated; must include openid and email."},
			orgScope,
		},
		Issuer: func(values map[string]string) string { return values["issuer"] },
	},
	{
		Protocol: "SAML", Kind: "Enterprise", Tone: "fuchsia", Icon: icons.FileText,
		Description: "SAML 2.0 identity providers with signed assertions.",
		Fields: []field{
			connectionName,
			{Key: "idp_entity_id", Label: "IdP entity ID", Type: typeText, Required: true, Immutable: true, Placeholder: "https://idp.example.com/metadata"},
			{Key: "sso_redirect_url", Label: "SSO URL", Type: typeURL, Required: true, Immutable: true, Placeholder: "https://idp.example.com/sso"},
			{Key: "x509_certificate_pem", Label: "Signing certificate", Type: typeTextarea, Required: true, Immutable: true, Placeholder: "-----BEGIN CERTIFICATE-----"},
			orgScope,
		},
		Issuer: func(values map[string]string) string { return values["idp_entity_id"] },
	},
}

func findProvider(protocol string) (provider, bool) {
	for _, current := range providers {
		if current.Protocol == protocol {
			return current, true
		}
	}
	return provider{}, false
}

// formFields returns the fields a form for this section and protocol posts.
func formFields(current section, protocol string) ([]field, bool) {
	if current.Key != "connections" {
		return current.Fields, true
	}
	found, ok := findProvider(protocol)
	return found.Fields, ok
}
