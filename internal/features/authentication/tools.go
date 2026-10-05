package authentication

import (
	"context"
	"maps"
	"net/url"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

const sectionHelp = "Call auth_describe_sections for each section's fields."

type overviewView struct {
	InstanceName       string          `json:"instanceName"`
	Slug               string          `json:"slug"`
	CanManage          bool            `json:"canManage" jsonschema:"Whether the caller may change this instance."`
	Issuer             string          `json:"issuer"`
	DiscoveryURL       string          `json:"discoveryUrl"`
	JWKSURL            string          `json:"jwksUrl"`
	UserinfoURL        string          `json:"userinfoUrl"`
	SignInURL          string          `json:"signInUrl"`
	ManagementAPI      string          `json:"managementApi" jsonschema:"Base URL of the ts-auth Management API for this instance."`
	HostedPages        []hostedURL     `json:"hostedPages"`
	Counts             map[string]int  `json:"counts" jsonschema:"Live (not revoked) records per section."`
	BlockedUsers       int             `json:"blockedUsers"`
	EnabledConnections []string        `json:"enabledConnections"`
	Strategies         []strategyView  `json:"strategies"`
	Branding           brandingView    `json:"branding"`
	Checklist          []checklistStep `json:"checklist"`
	RecentEvents       []eventView     `json:"recentEvents"`
}

type hostedURL struct {
	Label string `json:"label"`
	URL   string `json:"url"`
}

type checklistStep struct {
	Title   string `json:"title"`
	Detail  string `json:"detail"`
	Section string `json:"section" jsonschema:"Console section that completes this step."`
	Done    bool   `json:"done"`
}

type strategyView struct {
	Key         string `json:"key"`
	Label       string `json:"label"`
	Page        string `json:"page" jsonschema:"sign-in or mfa."`
	Description string `json:"description"`
	Enabled     bool   `json:"enabled"`
	FirstFactor bool   `json:"firstFactor" jsonschema:"First factors let users sign in on their own; at least one stays enabled."`
	Requires    string `json:"requires,omitempty" jsonschema:"Strategy that must be enabled first."`
}

type strategyList struct {
	Strategies []strategyView `json:"strategies"`
}

type brandingView struct {
	Name       string `json:"name"`
	LogoURL    string `json:"logoUrl,omitempty"`
	ThemeColor string `json:"themeColor,omitempty"`
}

type eventView struct {
	Action     string    `json:"action"`
	Target     string    `json:"target"`
	Actor      string    `json:"actor"`
	OccurredAt time.Time `json:"occurredAt"`
}

type eventList struct {
	Events []eventView `json:"events"`
}

type resourceView struct {
	ID          string            `json:"id"`
	Section     string            `json:"section"`
	Name        string            `json:"name"`
	Detail      string            `json:"detail,omitempty" jsonschema:"Secondary label such as the email, issuer, SCIM endpoint, or token route."`
	Tags        []string          `json:"tags,omitempty"`
	Status      string            `json:"status" jsonschema:"Active, Pending, Blocked, Disabled, or Revoked."`
	Values      map[string]string `json:"values" jsonschema:"Stored field values. Secrets are never stored; credential_configured reports whether one was submitted."`
	CallbackURL string            `json:"callbackUrl,omitempty" jsonschema:"Redirect URL to register with the upstream identity provider."`
	Actions     []string          `json:"actions,omitempty" jsonschema:"Actions auth_run_resource_action accepts for this record."`
	CreatedAt   time.Time         `json:"createdAt"`
}

type resourcePage struct {
	Section   string         `json:"section"`
	CanManage bool           `json:"canManage"`
	Resources []resourceView `json:"resources"`
}

type deletion struct {
	ID      string `json:"id"`
	Outcome string `json:"outcome" jsonschema:"revoked for invitations and management keys, deleted otherwise."`
}

type sectionCatalog struct {
	Sections   []sectionSpec  `json:"sections"`
	Providers  []providerSpec `json:"connectionProviders" jsonschema:"Connection providers; connections take the fields of the provider named in values.protocol."`
	Strategies []strategyView `json:"strategies" jsonschema:"Strategy keys for auth_set_strategy with their default state."`
}

type sectionSpec struct {
	Key         string      `json:"key"`
	Title       string      `json:"title"`
	Description string      `json:"description"`
	Editable    bool        `json:"editable" jsonschema:"Whether auth_save_resource may update existing records."`
	Revocable   bool        `json:"revocable" jsonschema:"Whether auth_delete_resource revokes instead of deleting."`
	Actions     []string    `json:"actions,omitempty"`
	Fields      []fieldSpec `json:"fields,omitempty"`
}

type providerSpec struct {
	Protocol    string      `json:"protocol"`
	Kind        string      `json:"kind"`
	Description string      `json:"description"`
	Fields      []fieldSpec `json:"fields"`
}

type fieldSpec struct {
	Key       string   `json:"key"`
	Label     string   `json:"label"`
	Type      string   `json:"type" jsonschema:"text, email, url, number, textarea, password (secret), organization or user (record ID), role, select, or choices (comma-separated)."`
	Hint      string   `json:"hint,omitempty"`
	Example   string   `json:"example,omitempty"`
	Required  bool     `json:"required"`
	Immutable bool     `json:"immutable" jsonschema:"Set at creation and ignored by updates."`
	Unique    bool     `json:"unique"`
	Options   []string `json:"options,omitempty"`
}

type sectionInput struct {
	core.Scope
	Section string `json:"section" jsonschema:"Record section; see auth_describe_sections."`
	Search  string `json:"search,omitempty" jsonschema:"Case-insensitive text matched against record IDs, names, and values."`
}

type saveResourceInput struct {
	core.Scope
	Section string            `json:"section" jsonschema:"Record section; see auth_describe_sections."`
	ID      string            `json:"id,omitempty" jsonschema:"Record ID to update. Omit to create a record."`
	Values  map[string]string `json:"values" jsonschema:"Field values keyed by field key from auth_describe_sections. Choices fields take comma-separated values. Connections also need protocol: Google, Microsoft, GitHub, OIDC, or SAML."`
}

type resourceInput struct {
	core.Scope
	Section string `json:"section" jsonschema:"Record section; see auth_describe_sections."`
	ID      string `json:"id" jsonschema:"Record ID from auth_list_resources."`
}

type actionInput struct {
	core.Scope
	Section string `json:"section" jsonschema:"Section of the record the action applies to."`
	ID      string `json:"id" jsonschema:"Record ID from auth_list_resources."`
	Action  string `json:"action" jsonschema:"block (users: toggles blocked and active), toggle (connections: toggles disabled and active), or rotate (directories: rotates the SCIM bearer token)."`
}

type strategyInput struct {
	core.Scope
	Strategy string `json:"strategy" jsonschema:"Strategy key; see auth_describe_sections."`
	Enabled  bool   `json:"enabled"`
}

type brandingInput struct {
	core.Scope
	Name       string `json:"name" jsonschema:"Instance name shown on the hosted pages, up to 128 characters."`
	LogoURL    string `json:"logoUrl,omitempty" jsonschema:"HTTPS logo URL. Empty clears the logo."`
	ThemeColor string `json:"themeColor,omitempty" jsonschema:"Six-digit hex color such as #4f46e5. Empty uses the default."`
}

// actionSections maps each record action to the one section it applies to.
var actionSections = map[string]string{"block": "users", "toggle": "connections", "rotate": "directories"}

func mountTools(tools *core.Tools, service Service) {
	sectionEnum := map[string][]string{"section": listSectionKeys()}
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_get_overview", Title: "Get authentication overview", ReadOnly: true,
		Description: "Summarize a project's ts-auth instance: issuer and hosted page URLs, enabled sign-in strategies, branding, record totals, setup checklist, and recent changes.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (overviewView, error) {
		return service.Overview(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_describe_sections", Title: "Describe authentication sections", ReadOnly: true,
		Description: "List the ts-auth record sections with their fields, validation rules, and actions, the SSO connection providers, and the sign-in strategies. Use it to build auth_save_resource values.",
	}, func(context.Context, core.Principal, struct{}) (sectionCatalog, error) {
		return describeSections(), nil
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_list_resources", Title: "List authentication records", ReadOnly: true,
		Enums:       sectionEnum,
		Description: "List a ts-auth record section, such as users, organizations, memberships, invitations, enrollment domains, SSO connections, SCIM directories, OAuth applications, JWT templates, management keys, or webhooks.",
	}, func(ctx context.Context, p core.Principal, in sectionInput) (resourcePage, error) {
		return service.ListResources(ctx, p, in.Section, in.Search)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_save_resource", Title: "Save authentication record",
		Enums:       sectionEnum,
		Description: "Create a ts-auth record, or update an editable record's mutable fields when id is given. " + sectionHelp + " Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in saveResourceInput) (resourceView, error) {
		current, err := listSection(in.Section)
		if err != nil {
			return resourceView{}, err
		}
		return service.SaveResource(ctx, p, in.Section, strings.TrimSpace(in.ID), formOf(current, in.Values))
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_delete_resource", Title: "Delete authentication record", Destructive: true,
		Enums:       sectionEnum,
		Description: "Revoke an invitation or management key, or delete any other ts-auth record. Deleting a user removes their memberships; deleting an organization removes its memberships, invitations, domains, directories, and connections.",
	}, func(ctx context.Context, p core.Principal, in resourceInput) (deletion, error) {
		return service.DeleteResource(ctx, p, in.Section, in.ID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_run_resource_action", Title: "Run authentication record action",
		Enums: map[string][]string{
			"section": slices.Sorted(maps.Values(actionSections)), "action": slices.Sorted(maps.Keys(actionSections)),
		},
		Description: "Block or unblock a user, enable or disable an SSO connection, or rotate a SCIM directory's bearer token.",
	}, func(ctx context.Context, p core.Principal, in actionInput) (resourceView, error) {
		return service.RunAction(ctx, p, in.Section, in.ID, in.Action)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_set_strategy", Title: "Set sign-in strategy", Idempotent: true,
		Enums:       map[string][]string{"strategy": strategyKeys()},
		Description: "Enable or disable a sign-in or multi-factor strategy. At least one first factor stays enabled, and disabling a strategy disables those that require it.",
	}, func(ctx context.Context, p core.Principal, in strategyInput) (strategyList, error) {
		return service.SetStrategy(ctx, p, in.Strategy, in.Enabled)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_update_branding", Title: "Update hosted page branding", Idempotent: true,
		Description: "Set the name, logo, and theme color of the instance's hosted sign-in pages.",
	}, func(ctx context.Context, p core.Principal, in brandingInput) (brandingView, error) {
		return service.SaveBranding(ctx, p, in.Name, in.LogoURL, in.ThemeColor)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_list_audit_events", Title: "List authentication audit events", ReadOnly: true,
		Description: "List the authentication console changes made in a project, newest first.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (eventList, error) {
		return service.AuditEvents(ctx, p)
	})
}

// formOf turns tool values into the form the console posts, splitting the
// comma-separated selections of choices fields.
func formOf(current section, values map[string]string) url.Values {
	form := make(url.Values, len(values))
	for key, value := range values {
		index := slices.IndexFunc(current.Fields, func(item field) bool { return item.Key == key })
		if index >= 0 && current.Fields[index].Type == typeChoices {
			form[key] = strings.Split(value, ",")
			continue
		}
		form.Set(key, value)
	}
	return form
}

func resourceViewOf(current section, resource core.AuthResource, names map[string]string, slug string) resourceView {
	row := describe(current, resource, names, slug)
	view := resourceView{
		ID: row.ID, Section: current.Key, Name: row.Name, Detail: row.Subtitle, Tags: row.Chips, Status: row.Status,
		Values: maps.Clone(resource.Values), CallbackURL: row.Callback, CreatedAt: row.Created,
	}
	for _, action := range row.Actions {
		view.Actions = append(view.Actions, action.Key)
	}
	if view.Values == nil {
		view.Values = map[string]string{}
	}
	return view
}

func strategyViews(settings core.AuthSettings) []strategyView {
	var views []strategyView
	for _, group := range strategyGroups {
		for _, current := range group.Strategies {
			views = append(views, strategyView{
				Key: current.Key, Label: current.Label, Page: group.Page, Description: current.Description,
				Enabled: current.get(settings), FirstFactor: current.FirstFactor, Requires: current.Requires,
			})
		}
	}
	return views
}

func eventViews(rows []eventRow) []eventView {
	views := make([]eventView, 0, len(rows))
	for _, row := range rows {
		views = append(views, eventView{Action: row.Action, Target: row.Target, Actor: row.Actor, OccurredAt: row.At})
	}
	return views
}

// listSectionKeys returns the keys of the sections that hold records.
func listSectionKeys() []string {
	var keys []string
	for _, current := range sections {
		if current.lists() {
			keys = append(keys, current.Key)
		}
	}
	return keys
}

func strategyKeys() []string {
	var keys []string
	for _, group := range strategyGroups {
		for _, current := range group.Strategies {
			keys = append(keys, current.Key)
		}
	}
	return keys
}

func describeSections() sectionCatalog {
	actions := make(map[string][]string, len(actionSections))
	for action, key := range actionSections {
		actions[key] = append(actions[key], action)
	}
	catalog := sectionCatalog{Strategies: strategyViews(defaultSettings)}
	for _, current := range sections {
		if !current.lists() {
			continue
		}
		spec := sectionSpec{
			Key: current.Key, Title: current.Title, Description: current.Description,
			Editable: current.Editable, Revocable: current.Revocable, Actions: actions[current.Key],
		}
		if current.Key != "connections" {
			spec.Fields = fieldSpecs(current.Fields)
		}
		catalog.Sections = append(catalog.Sections, spec)
	}
	for _, found := range providers {
		catalog.Providers = append(catalog.Providers, providerSpec{
			Protocol: found.Protocol, Kind: found.Kind, Description: found.Description, Fields: fieldSpecs(found.Fields),
		})
	}
	return catalog
}

func fieldSpecs(fields []field) []fieldSpec {
	specs := make([]fieldSpec, 0, len(fields))
	for _, item := range fields {
		spec := fieldSpec{
			Key: item.Key, Label: item.Label, Type: item.Type, Hint: item.Hint, Example: item.Placeholder,
			Required: item.Required, Immutable: item.Immutable, Unique: item.Unique,
		}
		for _, choice := range item.Options {
			spec.Options = append(spec.Options, choice.Value)
		}
		specs = append(specs, spec)
	}
	return specs
}
