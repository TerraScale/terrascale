// Package authentication owns the project-scoped ts-auth console: people,
// sign-in configuration, developer integrations, and hosted-page branding.
// Every change is stored on the project's core.AuthInstance.
package authentication

import (
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
)

// issuerOrigin is ts-auth's single global public origin; every instance's
// issuer, hosted pages, and Management API live under it.
const issuerOrigin = "https://auth.terrascale.tech"

// formError is a validation failure reported back on the page.
type formError string

func (e formError) Error() string { return string(e) }

// Mount registers the authentication console pages and their mutations.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /auth", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, "overview") })
	mux.HandleFunc("GET /auth/{section}", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, r.PathValue("section")) })
	mux.HandleFunc("POST /api/v1/auth-management/instance", saveBranding(deps))
	mux.HandleFunc("POST /api/v1/auth-management/settings", saveStrategy(deps))
	mux.HandleFunc("POST /api/v1/auth-management/{section}", saveResource(deps))
	mux.HandleFunc("POST /api/v1/auth-management/{section}/{resourceID}/delete", deleteResource(deps))
	mux.HandleFunc("POST /api/v1/auth-management/{section}/{resourceID}/actions/{action}", resourceAction(deps))
}

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps, key string) {
	current, exists := findSection(key)
	if !exists {
		http.NotFound(w, r)
		return
	}
	p, account, ok := signedIn(deps, w, r)
	if !ok {
		return
	}
	org, exists := account.Organizations[p.OrganizationID]
	if !exists {
		http.Error(w, "selected organization is unavailable", http.StatusForbidden)
		return
	}
	project, exists := org.Projects[p.ProjectID]
	if !exists || project.OrganizationID != org.ID {
		http.Error(w, "selected project is unavailable", http.StatusForbidden)
		return
	}
	query := r.URL.Query()
	data := buildPage(current, project, canManage(org, p.UserID), strings.TrimSpace(query.Get("q")), query.Get("page"))
	data.Saved, data.Error = query.Get("saved") == "1", query.Get("error")
	title := "Authentication"
	if key != "overview" {
		title += " · " + current.Title
	}
	page := core.Page{
		Layout: "shell", Title: title, Principal: p,
		View: func(view core.Page) templ.Component { return authenticationView(data, view.CSRFToken) },
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render authentication console", http.StatusInternalServerError)
	}
}

// saveResource creates a record, or updates the mutable fields of an
// existing one when the form carries its id.
func saveResource(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		key := r.PathValue("section")
		current, exists := findSection(key)
		if !exists || !current.lists() {
			http.NotFound(w, r)
			return
		}
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		resourceID := strings.TrimSpace(r.Form.Get("id"))
		now := time.Now().UTC()
		err := updateInstance(deps, p, now, func(instance *core.AuthInstance) (string, string, error) {
			if resourceID != "" {
				return updateRecord(instance, current, resourceID, r.Form)
			}
			return createRecord(instance, current, r.Form, now)
		})
		finish(w, r, key, err)
	}
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
		return "", "", core.ErrNotFound
	}
	if !current.Editable {
		return "", "", core.ErrForbidden
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

func deleteResource(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		key, id := r.PathValue("section"), r.PathValue("resourceID")
		current, exists := findSection(key)
		if !exists || !current.lists() {
			http.NotFound(w, r)
			return
		}
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		err := updateInstance(deps, p, time.Now().UTC(), func(instance *core.AuthInstance) (string, string, error) {
			index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool { return item.Section == key && item.ID == id })
			if index < 0 {
				return "", "", core.ErrNotFound
			}
			if current.Revocable {
				instance.Resources[index].Status = "Revoked"
				return "auth." + key + ".revoked", id, nil
			}
			instance.Resources = slices.Delete(instance.Resources, index, index+1)
			instance.Resources = slices.DeleteFunc(instance.Resources, func(item core.AuthResource) bool {
				return dependsOn(item, key, id)
			})
			return "auth." + key + ".removed", id, nil
		})
		finish(w, r, key, err)
	}
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

func resourceAction(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		key, id, action := r.PathValue("section"), r.PathValue("resourceID"), r.PathValue("action")
		if _, exists := findSection(key); !exists {
			http.NotFound(w, r)
			return
		}
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		now := time.Now().UTC()
		err := updateInstance(deps, p, now, func(instance *core.AuthInstance) (string, string, error) {
			index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool { return item.Section == key && item.ID == id })
			if index < 0 {
				return "", "", core.ErrNotFound
			}
			item := &instance.Resources[index]
			verb := action
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
				return "", "", core.ErrInvalid
			}
			return "auth." + key + "." + verb, item.ID, nil
		})
		finish(w, r, key, err)
	}
}

// saveStrategy switches one sign-in or multi-factor strategy.
func saveStrategy(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		key, on := r.Form.Get("setting"), r.Form.Get("enabled") == "true"
		_, group, exists := findStrategy(key)
		if !exists {
			http.NotFound(w, r)
			return
		}
		now := time.Now().UTC()
		err := updateInstance(deps, p, now, func(instance *core.AuthInstance) (string, string, error) {
			settings, message := applyStrategy(effectiveSettings(*instance), key, on)
			if message != "" {
				return "", "", formError(message)
			}
			settings.UpdatedAt = now
			instance.Settings = settings
			state := "disabled"
			if on {
				state = "enabled"
			}
			return "auth.settings." + key + "_" + state, p.ProjectID, nil
		})
		finish(w, r, group.Page, err)
	}
}

func saveBranding(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		name := strings.TrimSpace(r.Form.Get("name"))
		logo := strings.TrimSpace(r.Form.Get("logo_url"))
		color := strings.ToLower(strings.TrimSpace(r.Form.Get("theme_color")))
		err := updateInstance(deps, p, time.Now().UTC(), func(instance *core.AuthInstance) (string, string, error) {
			if name == "" || utf8.RuneCountInString(name) > 128 || (logo != "" && !validHTTPSURL(logo)) || (color != "" && !themeColorPattern.MatchString(color)) {
				return "", "", formError("Enter a name, an HTTPS logo URL, and a six-digit hex theme color.")
			}
			instance.Name, instance.LogoURL, instance.ThemeColor = name, logo, color
			return "auth.instance.branding_saved", p.ProjectID, nil
		})
		finish(w, r, "branding", err)
	}
}

// updateInstance applies change to the principal's project instance and
// records the audit event it names, all in one store update.
func updateInstance(deps core.Deps, p core.Principal, now time.Time, change func(*core.AuthInstance) (action, target string, err error)) error {
	return deps.Store.UpdateAs(p, func(account *core.Account) error {
		org, err := account.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if !canManage(org, p.UserID) {
			return core.ErrForbidden
		}
		project, exists := org.Projects[p.ProjectID]
		if !exists || project.OrganizationID != org.ID {
			return core.ErrNotFound
		}
		instance := project.AuthInstance
		action, target, err := change(&instance)
		if err != nil {
			return err
		}
		event := core.AuditEvent{
			ID: core.NewID("evt"), OrganizationID: p.OrganizationID, ProjectID: p.ProjectID,
			Actor: account.User.Email, Action: action, Target: target, TargetType: "authentication console",
			Risk: "low", Source: "dashboard", OccurredAt: now,
		}
		instance.AuditEvents = append(instance.AuditEvents, event)
		account.AuditEvents = append(account.AuditEvents, event)
		project.AuthInstance = instance
		org.Projects[project.ID] = project
		account.Organizations[org.ID] = org
		return nil
	})
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
	for key, value := range existing {
		stored[key] = value
	}
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

// authorize verifies CSRF, the session, and manage rights, and parses the form.
func authorize(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if !deps.VerifyCSRF(w, r) {
		return core.Principal{}, false
	}
	p, account, ok := signedIn(deps, w, r)
	if !ok {
		return core.Principal{}, false
	}
	if !canManage(account.Organizations[p.OrganizationID], p.UserID) {
		http.Error(w, "forbidden", http.StatusForbidden)
		return core.Principal{}, false
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "invalid form", http.StatusBadRequest)
		return core.Principal{}, false
	}
	return p, true
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, core.Account, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, core.Account{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, core.Account{}, false
	}
	account, err := deps.Store.SnapshotAs(p)
	if err != nil {
		writeError(w, err)
		return core.Principal{}, core.Account{}, false
	}
	return p, account, true
}

func canManage(org core.Organization, userID string) bool {
	return org.OwnerUserID == userID || org.CanManage(userID)
}

// finish redirects back to the section with a saved flag or the form error.
func finish(w http.ResponseWriter, r *http.Request, key string, err error) {
	var message formError
	switch {
	case errors.As(err, &message):
		redirect(w, r, key, url.Values{"error": {string(message)}})
	case err != nil:
		writeError(w, err)
	default:
		redirect(w, r, key, url.Values{"saved": {"1"}})
	}
}

func redirect(w http.ResponseWriter, r *http.Request, key string, query url.Values) {
	http.Redirect(w, r, "/auth/"+url.PathEscape(key)+"?"+query.Encode(), http.StatusSeeOther)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
