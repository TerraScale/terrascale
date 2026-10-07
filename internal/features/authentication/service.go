package authentication

import (
	"cmp"
	"context"
	"fmt"
	"maps"
	"net/url"
	"slices"
	"strings"
	"time"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
)

// formError is a validation failure reported back on the page.
type formError string

func (e formError) Error() string { return string(e) }

// Is lets callers treat form validation failures as invalid input.
func (e formError) Is(target error) bool { return target == core.ErrInvalid }

// Service manages the ts-auth instance of a principal's selected project.
// HTTP handlers and MCP tools both call it.
type Service struct {
	deps   core.Deps
	origin string
	emails EmailBackend
}

// scope returns the principal's selected organization and project.
func (s Service) scope(ctx context.Context, p core.Principal) (core.Organization, core.Project, error) {
	if p.ProjectID == "" {
		return core.Organization{}, core.Project{}, core.ErrNotFound
	}
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Organization{}, core.Project{}, err
	}
	org, err := account.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		return core.Organization{}, core.Project{}, err
	}
	project, err := account.ProjectFor(p, org.ID, p.ProjectID)
	return org, project, err
}

// Overview summarizes the instance: endpoints, strategies, branding,
// totals, setup progress, and the latest changes.
func (s Service) Overview(ctx context.Context, p core.Principal) (overviewView, error) {
	org, project, err := s.scope(ctx, p)
	if err != nil {
		return overviewView{}, err
	}
	data := buildPage(mustSection("overview"), project, s.origin, org.CanManage(p.UserID), "", "")
	overview := overviewView{
		InstanceName: data.InstanceName, Slug: data.Slug, CanManage: data.CanManage,
		Issuer: data.issuer(), DiscoveryURL: data.issuer() + "/.well-known/openid-configuration",
		JWKSURL: data.issuer() + "/.well-known/jwks.json", UserinfoURL: data.issuer() + "/oauth/userinfo",
		SignInURL: data.hosted("/sign-in"), ManagementAPI: data.Origin + "/v1/" + data.Slug,
		Counts: data.Counts, BlockedUsers: data.Blocked, Strategies: strategyViews(data.Settings),
		Branding:     brandingView{Name: data.InstanceName, LogoURL: project.AuthInstance.LogoURL, ThemeColor: project.AuthInstance.ThemeColor},
		RecentEvents: eventViews(data.Events),
	}
	for _, page := range hostedPages {
		overview.HostedPages = append(overview.HostedPages, hostedURL{Label: page.Label, URL: data.hosted(page.Path)})
	}
	for _, connection := range data.Connections {
		overview.EnabledConnections = append(overview.EnabledConnections, connection.Name)
	}
	for _, step := range data.Checklist {
		overview.Checklist = append(overview.Checklist, checklistStep{
			Title: step.Title, Detail: step.Detail, Section: strings.TrimPrefix(step.URL, "/auth/"), Done: step.Done,
		})
	}
	return overview, nil
}

// ListResources returns the records of one list section, optionally
// filtered by a case-insensitive search over their values.
func (s Service) ListResources(ctx context.Context, p core.Principal, key, search string) (resourcePage, error) {
	current, err := listSection(key)
	if err != nil {
		return resourcePage{}, err
	}
	org, project, err := s.scope(ctx, p)
	if err != nil {
		return resourcePage{}, err
	}
	instance := project.AuthInstance
	names := recordNames(instance)
	slug := cmp.Or(project.Slug, project.ID)
	list := resourcePage{Section: key, Resources: make([]resourceView, 0), CanManage: org.CanManage(p.UserID)}
	for _, resource := range instance.Resources {
		if resource.Section == key && matches(resource, names, strings.TrimSpace(search)) {
			list.Resources = append(list.Resources, resourceViewOf(current, resource, names, s.origin, slug))
		}
	}
	slices.SortFunc(list.Resources, func(a, b resourceView) int {
		return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name))
	})
	return list, nil
}

// SaveResource creates a record, or updates the mutable fields of the
// record id names. Form keys are the section's field keys; connections also
// carry their provider in "protocol".
func (s Service) SaveResource(ctx context.Context, p core.Principal, key, id string, form url.Values) (resourceView, error) {
	current, err := listSection(key)
	if err != nil {
		return resourceView{}, err
	}
	now := time.Now().UTC()
	target, err := s.update(ctx, p, now, func(instance *core.AuthInstance) (string, string, error) {
		if id != "" {
			return updateRecord(instance, current, id, form)
		}
		return createRecord(instance, current, form, now)
	})
	if err != nil {
		return resourceView{}, err
	}
	return s.resource(ctx, p, current, target)
}

// DeleteResource revokes a revocable record (invitations and management
// keys) and deletes any other, cascading to records that depend on it.
func (s Service) DeleteResource(ctx context.Context, p core.Principal, key, id string) (deletion, error) {
	current, err := listSection(key)
	if err != nil {
		return deletion{}, err
	}
	outcome := "deleted"
	_, err = s.update(ctx, p, time.Now().UTC(), func(instance *core.AuthInstance) (string, string, error) {
		index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool { return item.Section == key && item.ID == id })
		if index < 0 {
			return "", "", fmt.Errorf("%w: %s record %q", core.ErrNotFound, key, id)
		}
		if current.Revocable {
			instance.Resources[index].Status, outcome = "Revoked", "revoked"
			return "auth." + key + ".revoked", id, nil
		}
		instance.Resources = slices.Delete(instance.Resources, index, index+1)
		instance.Resources = slices.DeleteFunc(instance.Resources, func(item core.AuthResource) bool {
			return dependsOn(item, key, id)
		})
		return "auth." + key + ".removed", id, nil
	})
	if err != nil {
		return deletion{}, err
	}
	return deletion{ID: id, Outcome: outcome}, nil
}

// RunAction toggles a user's block, toggles a connection on or off, or
// rotates a directory's SCIM token.
func (s Service) RunAction(ctx context.Context, p core.Principal, key, id, action string) (resourceView, error) {
	current, err := listSection(key)
	if err != nil {
		return resourceView{}, err
	}
	now := time.Now().UTC()
	target, err := s.update(ctx, p, now, func(instance *core.AuthInstance) (string, string, error) {
		index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool { return item.Section == key && item.ID == id })
		if index < 0 {
			return "", "", fmt.Errorf("%w: %s record %q", core.ErrNotFound, key, id)
		}
		item := &instance.Resources[index]
		var verb string
		switch key + ":" + action {
		case "users:block":
			if item.Status == "Blocked" {
				item.Status, verb = "Active", "unblocked"
			} else {
				item.Status, verb = "Blocked", "blocked"
			}
		case "connections:toggle":
			if item.Status == "Disabled" {
				item.Status, verb = "Active", "enabled"
			} else {
				item.Status, verb = "Disabled", "disabled"
			}
		case "directories:rotate":
			item.Values["token_rotated_at"] = now.Format(time.RFC3339)
			verb = "token_rotated"
		default:
			return "", "", core.Invalid(fmt.Sprintf("%s records have no %q action", key, action))
		}
		return "auth." + key + "." + verb, item.ID, nil
	})
	if err != nil {
		return resourceView{}, err
	}
	return s.resource(ctx, p, current, target)
}

// SetStrategy switches one sign-in or multi-factor strategy, keeping at
// least one first factor and turning off strategies that required it.
func (s Service) SetStrategy(ctx context.Context, p core.Principal, key string, on bool) (strategyList, error) {
	if _, _, exists := findStrategy(key); !exists {
		return strategyList{}, fmt.Errorf("%w: strategy %q", core.ErrNotFound, key)
	}
	now := time.Now().UTC()
	var settings core.AuthSettings
	_, err := s.update(ctx, p, now, func(instance *core.AuthInstance) (string, string, error) {
		next, message := applyStrategy(effectiveSettings(*instance), key, on)
		if message != "" {
			return "", "", formError(message)
		}
		next.UpdatedAt = now
		instance.Settings, settings = next, next
		state := "disabled"
		if on {
			state = "enabled"
		}
		return "auth.settings." + key + "_" + state, p.ProjectID, nil
	})
	if err != nil {
		return strategyList{}, err
	}
	return strategyList{Strategies: strategyViews(settings)}, nil
}

// SaveBranding sets the hosted pages' name, logo, and theme color.
func (s Service) SaveBranding(ctx context.Context, p core.Principal, name, logo, color string) (brandingView, error) {
	name, logo = strings.TrimSpace(name), strings.TrimSpace(logo)
	color = strings.ToLower(strings.TrimSpace(color))
	_, err := s.update(ctx, p, time.Now().UTC(), func(instance *core.AuthInstance) (string, string, error) {
		if name == "" || utf8.RuneCountInString(name) > 128 || (logo != "" && !validHTTPSURL(logo)) || (color != "" && !themeColorPattern.MatchString(color)) {
			return "", "", formError("Enter a name, an HTTPS logo URL, and a six-digit hex theme color.")
		}
		instance.Name, instance.LogoURL, instance.ThemeColor = name, logo, color
		return "auth.instance.branding_saved", p.ProjectID, nil
	})
	if err != nil {
		return brandingView{}, err
	}
	return brandingView{Name: name, LogoURL: logo, ThemeColor: color}, nil
}

// AuditEvents lists the instance's authentication changes, newest first.
func (s Service) AuditEvents(ctx context.Context, p core.Principal) (eventList, error) {
	_, project, err := s.scope(ctx, p)
	if err != nil {
		return eventList{}, err
	}
	return eventList{Events: eventViews(events(project.AuthInstance, 0))}, nil
}

func (s Service) resource(ctx context.Context, p core.Principal, current section, id string) (resourceView, error) {
	_, project, err := s.scope(ctx, p)
	if err != nil {
		return resourceView{}, err
	}
	instance := project.AuthInstance
	index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool { return item.Section == current.Key && item.ID == id })
	if index < 0 {
		return resourceView{}, core.ErrNotFound
	}
	return resourceViewOf(current, instance.Resources[index], recordNames(instance), s.origin, cmp.Or(project.Slug, project.ID)), nil
}

// update applies change to the principal's project instance and records
// the audit event it names, all in one store update. It returns the
// event's target.
func (s Service) update(ctx context.Context, p core.Principal, now time.Time, change func(*core.AuthInstance) (action, target string, err error)) (string, error) {
	var target string
	err := s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		org, err := account.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if !org.CanManage(p.UserID) {
			return core.ErrForbidden
		}
		project, exists := org.Projects[p.ProjectID]
		if !exists || project.OrganizationID != org.ID {
			return core.ErrNotFound
		}
		instance := project.AuthInstance
		action, changed, err := change(&instance)
		if err != nil {
			return err
		}
		target = changed
		event := account.RecordAudit(p, action, target, "authentication console", now)
		instance.AuditEvents = append(instance.AuditEvents, event)
		project.AuthInstance = instance
		org.Projects[project.ID] = project
		account.Organizations[org.ID] = org
		return nil
	})
	return target, err
}

func listSection(key string) (section, error) {
	current, exists := findSection(key)
	if !exists || !current.lists() {
		return section{}, fmt.Errorf("%w: authentication section %q", core.ErrNotFound, key)
	}
	return current, nil
}

func mustSection(key string) section {
	current, _ := findSection(key)
	return current
}

func createRecord(instance *core.AuthInstance, current section, form url.Values, now time.Time) (string, string, error) {
	protocol := strings.TrimSpace(form.Get("protocol"))
	fields, ok := formFields(current, protocol)
	if !ok {
		return "", "", formError("Choose a connection provider.")
	}
	values := formValues(fields, form)
	if message := validate(current, fields, values); message != "" {
		return "", "", formError(message)
	}
	if err := checkReferences(*instance, current, fields, values, ""); err != nil {
		return "", "", err
	}
	resource := core.AuthResource{
		ID: core.NewID(current.IDPrefix), Section: current.Key, Status: "Active",
		Values: storedValues(fields, values, nil), CreatedAt: now,
	}
	switch current.Key {
	case "connections":
		found, _ := findProvider(protocol)
		resource.Values["protocol"] = found.Protocol
		resource.Values["issuer"] = found.Issuer(values)
		if found.Protocol == "OIDC" && values["scopes"] == "" {
			resource.Values["scopes"] = "openid email profile"
		}
	case "domains":
		resource.Values["domain"], _ = normalizeDomain(values["domain"])
	case "invitations":
		resource.Status = "Pending"
	}
	instance.Resources = append(instance.Resources, resource)
	return "auth." + current.Key + ".created", resource.ID, nil
}

func updateRecord(instance *core.AuthInstance, current section, resourceID string, form url.Values) (string, string, error) {
	index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool {
		return item.Section == current.Key && item.ID == resourceID
	})
	if index < 0 {
		return "", "", fmt.Errorf("%w: %s record %q", core.ErrNotFound, current.Key, resourceID)
	}
	if !current.Editable {
		return "", "", fmt.Errorf("%w: %s records cannot be edited", core.ErrForbidden, current.Key)
	}
	existing := instance.Resources[index]
	fields, _ := formFields(current, existing.Values["protocol"])
	mutable := make([]field, 0, len(fields))
	for _, item := range fields {
		if !item.Immutable {
			// A stored secret is kept unless a replacement is submitted.
			item.Required = item.Required && item.Type != typeSecret
			mutable = append(mutable, item)
		}
	}
	values := formValues(mutable, form)
	if message := validate(current, mutable, values); message != "" {
		return "", "", formError(message)
	}
	if err := checkReferences(*instance, current, mutable, values, resourceID); err != nil {
		return "", "", err
	}
	instance.Resources[index].Values = storedValues(mutable, values, existing.Values)
	return "auth." + current.Key + ".updated", resourceID, nil
}

// dependsOn reports whether a record belongs to a deleted user or
// organization, so deleting them cascades like ts-auth does.
func dependsOn(item core.AuthResource, section, id string) bool {
	switch section {
	case "users":
		return item.Section == "memberships" && item.Values["user"] == id
	case "organizations":
		return item.Values["organization"] == id && slices.Contains([]string{"memberships", "invitations", "domains", "directories", "connections"}, item.Section)
	}
	return false
}

// formValues reads the trimmed values of fields; checkbox groups join their
// selections with commas.
func formValues(fields []field, form url.Values) map[string]string {
	values := make(map[string]string, len(fields))
	for _, item := range fields {
		if item.Type == typeChoices {
			selected := make([]string, 0)
			for _, value := range form[item.Key] {
				if value = strings.TrimSpace(value); value != "" && !slices.Contains(selected, value) {
					selected = append(selected, value)
				}
			}
			values[item.Key] = strings.Join(selected, ",")
			continue
		}
		values[item.Key] = strings.TrimSpace(form.Get(item.Key))
	}
	if value, ok := values["redirect_uris"]; ok {
		values["redirect_uris"] = strings.Join(strings.Fields(value), "\n")
	}
	return values
}

// checkReferences enforces instance-wide uniqueness and that organization
// and user references point at existing records.
func checkReferences(instance core.AuthInstance, current section, fields []field, values map[string]string, selfID string) error {
	for _, item := range fields {
		value := values[item.Key]
		if value == "" {
			continue
		}
		switch item.Type {
		case typeOrganization, typeUser:
			target := "organizations"
			if item.Type == typeUser {
				target = "users"
			}
			if !slices.ContainsFunc(instance.Resources, func(r core.AuthResource) bool { return r.Section == target && r.ID == value }) {
				return formError("Choose an existing " + strings.ToLower(item.Label) + ".")
			}
		}
		if item.Unique && slices.ContainsFunc(instance.Resources, func(r core.AuthResource) bool {
			return r.Section == current.Key && r.ID != selfID && strings.EqualFold(r.Values[item.Key], value)
		}) {
			return formError("Another " + current.Noun + " already uses this " + strings.ToLower(item.Label) + ".")
		}
	}
	if current.Key == "memberships" && selfID == "" && slices.ContainsFunc(instance.Resources, func(r core.AuthResource) bool {
		return r.Section == "memberships" && r.Values["organization"] == values["organization"] && r.Values["user"] == values["user"]
	}) {
		return formError("This user is already a member of the organization.")
	}
	return nil
}

// storedValues merges submitted values over existing ones. Secrets are
// never stored; only the fact that one is configured is.
func storedValues(fields []field, values, existing map[string]string) map[string]string {
	stored := make(map[string]string, len(existing)+len(values))
	maps.Copy(stored, existing)
	for _, item := range fields {
		value := values[item.Key]
		if item.Type == typeSecret {
			if value != "" {
				stored["credential_configured"] = "true"
			}
			continue
		}
		stored[item.Key] = value
	}
	return stored
}
