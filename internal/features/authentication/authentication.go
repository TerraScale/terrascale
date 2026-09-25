// Package authentication owns the project-scoped ts-auth console simulation.
package authentication

import (
	"encoding/json/v2"
	"errors"
	"net/http"
	"net/mail"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
)

const pageSize = 10

var themeColorPattern = regexp.MustCompile(`^#[0-9a-fA-F]{6}$`)

type field struct {
	Key      string
	Label    string
	Type     string
	Required bool
}

type section struct {
	Key         string
	Title       string
	Description string
	Fields      []field
}

var sections = []section{
	{Key: "overview", Title: "Overview", Description: "Project identity workspace."},
	{Key: "users", Title: "Users", Description: "Demo user profile records.", Fields: []field{{"name", "Full name", "text", true}, {"email", "Email address", "email", true}, {"username", "Username", "text", false}}},
	{Key: "organizations", Title: "Organizations", Description: "Demo customer organization records.", Fields: []field{{"name", "Organization name", "text", true}, {"slug", "Slug", "text", true}}},
	{Key: "memberships", Title: "Memberships", Description: "Demo organization membership records.", Fields: []field{{"name", "Membership label", "text", true}, {"organization", "Organization ID", "text", true}, {"user", "User ID", "text", true}, {"role", "Role", "text", true}}},
	{Key: "connections", Title: "Connections", Description: "Demo sign-in provider configuration.", Fields: []field{{"name", "Connection name", "text", true}, {"protocol", "Protocol", "text", true}, {"issuer", "Issuer URL", "url", true}, {"client_id", "Client ID", "text", false}, {"client_secret", "Client secret", "password", false}}},
	{Key: "clients", Title: "OAuth clients", Description: "Demo OAuth client configuration.", Fields: []field{{"name", "Client name", "text", true}, {"type", "Application type", "text", true}, {"redirect_uris", "Redirect URIs", "textarea", false}}},
	{Key: "templates", Title: "JWT templates", Description: "Demo token claim configuration.", Fields: []field{{"name", "Template name", "text", true}, {"claims", "Claims JSON", "textarea", true}, {"lifetime_seconds", "Lifetime in seconds", "number", true}}},
	{Key: "keys", Title: "Management keys", Description: "Demo records only; this console does not issue usable ts-auth credentials.", Fields: []field{{"name", "Key name", "text", true}, {"scopes", "Scopes", "text", true}}},
	{Key: "webhooks", Title: "Webhooks", Description: "Demo delivery endpoint configuration.", Fields: []field{{"url", "Endpoint URL", "url", true}, {"events", "Events", "text", true}}},
	{Key: "audit", Title: "Audit log", Description: "Changes made in this dashboard simulation."},
	{Key: "settings", Title: "Settings", Description: "Hosted sign-in branding settings."},
	{Key: "quickstart", Title: "Quickstart", Description: "Integration guide for a connected ts-auth provider."},
}

type resourceRow struct {
	ID       string
	Name     string
	Status   string
	Created  time.Time
	Details  []string
	Fields   []fieldValue
	Editable bool
}

type fieldValue struct {
	Field field
	Value string
}

type eventRow struct {
	Action string
	Target string
	Actor  string
	At     time.Time
}

type pageData struct {
	Section       string
	Title         string
	Description   string
	Sections      []section
	Fields        []field
	Resources     []resourceRow
	Events        []eventRow
	Search        string
	Page          int
	PageCount     int
	Total         int
	HasPrevious   bool
	HasNext       bool
	Previous      int
	Next          int
	CanManage     bool
	Demo          bool
	Instance      core.AuthInstance
	Error         string
	Saved         bool
	ProfileName   string
	ProfileEmail  string
	ActiveUsers   int
	Organizations int
	Connections   int
	Clients       int
	Webhooks      int
}

// Mount registers the local authentication console and its simulation-only mutations.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /auth", page(deps, "overview"))
	mux.HandleFunc("GET /auth/{section}", pageByPath(deps))
	mux.HandleFunc("POST /api/v1/auth-management/{section}", saveResource(deps))
	mux.HandleFunc("POST /api/v1/auth-management/instance", saveBranding(deps))
	mux.HandleFunc("POST /api/v1/auth-management/{section}/{resourceID}/delete", deleteResource(deps))
	mux.HandleFunc("POST /api/v1/auth-management/{section}/{resourceID}/actions/{action}", resourceAction(deps))
}

func page(deps core.Deps, key string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, key) }
}

func pageByPath(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, r.PathValue("section")) }
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
	instance := project.AuthInstance
	data := pageData{
		Section: key, Title: current.Title, Description: current.Description,
		Sections: sections, Fields: current.Fields, Search: strings.TrimSpace(r.URL.Query().Get("q")),
		CanManage: canManage(org, p.UserID), Demo: deps.Demo, Instance: instance,
		ProfileName: account.User.Name, ProfileEmail: account.User.Email,
		Saved: r.URL.Query().Get("saved") == "1", Error: r.URL.Query().Get("error"),
		Page: 1, PageCount: 1, Previous: 1, Next: 1,
	}

	for _, resource := range instance.Resources {
		switch resource.Section {
		case "users":
			data.ActiveUsers++
		case "organizations":
			data.Organizations++
		case "connections":
			data.Connections++
		case "clients":
			data.Clients++
		case "webhooks":
			data.Webhooks++
		}
	}
	if key == "audit" {
		for _, item := range instance.AuditEvents {
			if item.OrganizationID == p.OrganizationID && item.ProjectID == p.ProjectID && strings.HasPrefix(item.Action, "auth.") {
				data.Events = append(data.Events, eventRow{Action: item.Action, Target: item.Target, Actor: item.Actor, At: item.OccurredAt})
			}
		}
		slices.SortFunc(data.Events, func(a, b eventRow) int { return b.At.Compare(a.At) })
	} else {
		rows := make([]resourceRow, 0)
		for _, resource := range instance.Resources {
			if resource.Section != key || !matches(resource, data.Search) {
				continue
			}
			name := resource.Values["name"]
			if name == "" {
				name = resource.Values["email"]
			}
			if name == "" {
				name = resource.Values["url"]
			}
			if name == "" {
				name = resource.ID
			}
			details := make([]string, 0, len(current.Fields))
			for _, item := range current.Fields {
				if value := resource.Values[item.Key]; value != "" && item.Key != "client_secret" {
					details = append(details, item.Label+": "+value)
				}
			}
			formFields := make([]fieldValue, 0, len(current.Fields))
			for _, item := range current.Fields {
				value := resource.Values[item.Key]
				if item.Key == "client_secret" {
					value = ""
				}
				formFields = append(formFields, fieldValue{Field: item, Value: value})
			}
			editable := slices.Contains([]string{"users", "organizations", "memberships", "connections", "clients", "templates"}, key)
			rows = append(rows, resourceRow{ID: resource.ID, Name: name, Status: resource.Status, Created: resource.CreatedAt, Details: details, Fields: formFields, Editable: editable})
		}
		slices.SortFunc(rows, func(a, b resourceRow) int { return strings.Compare(a.Name, b.Name) })
		data.Total = len(rows)
		data.PageCount = max(1, (len(rows)+pageSize-1)/pageSize)
		data.Page = core.ClampPage(r.URL.Query().Get("page"), data.PageCount)
		start := (data.Page - 1) * pageSize
		data.Resources = rows[start:min(start+pageSize, len(rows))]
		data.Previous, data.Next = max(1, data.Page-1), min(data.PageCount, data.Page+1)
		data.HasPrevious, data.HasNext = data.Page > 1, data.Page < data.PageCount
	}

	page := core.Page{
		Layout: "shell", Title: "Authentication · " + current.Title, Principal: p,
		View: func(view core.Page) templ.Component {
			return authenticationView(data, view.Shell.ProjectName, view.CSRFToken)
		},
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render authentication console", http.StatusInternalServerError)
	}
}

func saveResource(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		key := r.PathValue("section")
		current, exists := findSection(key)
		if !exists || len(current.Fields) == 0 {
			http.NotFound(w, r)
			return
		}
		if !canManage(account.Organizations[p.OrganizationID], p.UserID) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		values := make(map[string]string, len(current.Fields))
		for _, item := range current.Fields {
			values[item.Key] = strings.TrimSpace(r.Form.Get(item.Key))
		}
		if message := validate(current, values); message != "" {
			redirect(w, r, key, "error", message)
			return
		}
		resourceID := strings.TrimSpace(r.Form.Get("id"))
		auditVerb := "created"
		if resourceID != "" {
			auditVerb = "updated"
		}
		now := time.Now().UTC()
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
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
			found := false
			if resourceID != "" {
				for i := range instance.Resources {
					if instance.Resources[i].ID != resourceID || instance.Resources[i].Section != key {
						continue
					}
					if key == "users" || key == "organizations" || key == "memberships" || key == "connections" || key == "clients" || key == "templates" {
						instance.Resources[i].Values = safeValues(values, instance.Resources[i].Values)
						instance.Resources[i].Status = "Active"
						found = true
						break
					}
					return core.ErrForbidden
				}
				if !found {
					return core.ErrNotFound
				}
			} else {
				status := "Active"
				if key == "invitations" {
					status = "Pending"
				}
				resourceID = core.NewID(strings.TrimSuffix(key, "s"))
				instance.Resources = append(instance.Resources, core.AuthResource{ID: resourceID, Section: key, Status: status, Values: safeValues(values, nil), CreatedAt: now})
			}
			project.AuthInstance = instance
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			appendAudit(account, p, "auth."+key+"."+auditVerb, resourceID, now)
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirect(w, r, key, "saved", "1")
	}
}

func deleteResource(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		key, id := r.PathValue("section"), r.PathValue("resourceID")
		if _, exists := findSection(key); !exists || key == "audit" || key == "overview" || key == "settings" || key == "quickstart" {
			http.NotFound(w, r)
			return
		}
		if !canManage(account.Organizations[p.OrganizationID], p.UserID) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		now := time.Now().UTC()
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
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
			index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool { return item.Section == key && item.ID == id })
			if index < 0 {
				return core.ErrNotFound
			}
			item := instance.Resources[index]
			if key == "sessions" || key == "keys" || key == "invitations" {
				instance.Resources[index].Status = "Revoked"
			} else {
				instance.Resources = slices.Delete(instance.Resources, index, index+1)
			}
			project.AuthInstance = instance
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			appendAudit(account, p, "auth."+key+".removed", item.ID, now)
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirect(w, r, key, "saved", "1")
	}
}

func resourceAction(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		key, id, action := r.PathValue("section"), r.PathValue("resourceID"), r.PathValue("action")
		if !canManage(account.Organizations[p.OrganizationID], p.UserID) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		now := time.Now().UTC()
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
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
			index := slices.IndexFunc(instance.Resources, func(item core.AuthResource) bool { return item.Section == key && item.ID == id })
			if index < 0 {
				return core.ErrNotFound
			}
			item := &instance.Resources[index]
			switch key + ":" + action {
			case "users:block":
				if strings.EqualFold(item.Status, "blocked") {
					item.Status = "Active"
				} else {
					item.Status = "Blocked"
				}
			case "keys:revoke", "sessions:revoke", "invitations:revoke":
				item.Status = "Revoked"
			case "webhooks:test", "directories:rotate", "deliveries:replay":
				return core.ErrInvalid
			default:
				return core.ErrInvalid
			}
			project.AuthInstance = instance
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			appendAudit(account, p, "auth."+key+"."+action, item.ID, now)
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirect(w, r, key, "saved", "1")
	}
}

func saveBranding(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		org, exists := account.Organizations[p.OrganizationID]
		if !exists || !canManage(org, p.UserID) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		name := strings.TrimSpace(r.Form.Get("name"))
		logo := strings.TrimSpace(r.Form.Get("logo_url"))
		color := strings.TrimSpace(r.Form.Get("theme_color"))
		if name == "" || utf8.RuneCountInString(name) > 128 || (logo != "" && !validHTTPSURL(logo)) || (color != "" && !themeColorPattern.MatchString(color)) {
			redirect(w, r, "settings", "error", "Enter a name, an HTTPS logo URL, and a six-digit hex theme color.")
			return
		}
		now := time.Now().UTC()
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
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
			instance.Name, instance.LogoURL, instance.ThemeColor = name, logo, color
			project.AuthInstance = instance
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			appendAudit(account, p, "auth.instance.branding_saved", p.ProjectID, now)
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirect(w, r, "settings", "saved", "1")
	}
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, core.Account, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, core.Account{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		http.Redirect(w, r, "/login", http.StatusSeeOther)
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

func findSection(key string) (section, bool) {
	for _, current := range sections {
		if current.Key == key {
			return current, true
		}
	}
	return section{}, false
}

func validate(current section, values map[string]string) string {
	for _, item := range current.Fields {
		value := values[item.Key]
		if item.Required && value == "" {
			return item.Label + " is required."
		}
		if len(value) > 16384 {
			return item.Label + " is too long."
		}
		if value == "" {
			continue
		}
		switch item.Type {
		case "email":
			address, err := mail.ParseAddress(value)
			if err != nil || address.Address != value || len(value) > 320 {
				return "Enter a valid email address."
			}
		case "url":
			if !validHTTPSURL(value) {
				return item.Label + " must be an absolute HTTPS URL."
			}
		case "number":
			number, err := strconv.Atoi(value)
			if err != nil || number < 1 || number > 86400 {
				return item.Label + " must be between 1 and 86400."
			}
		case "textarea":
			if item.Key == "claims" {
				if !jsonObject(value) {
					return "Claims must be a valid JSON object."
				}
			}
		}
	}
	return ""
}

func validHTTPSURL(value string) bool {
	parsed, err := url.Parse(value)
	return err == nil && parsed.IsAbs() && parsed.Scheme == "https" && parsed.Host != "" && parsed.User == nil
}

func jsonObject(value string) bool {
	var object map[string]any
	return json.Unmarshal([]byte(value), &object) == nil && object != nil
}

func safeValues(values, existing map[string]string) map[string]string {
	stored := make(map[string]string, len(values)+1)
	for key, value := range values {
		if key != "client_secret" {
			stored[key] = value
		}
	}
	if existing["credential_configured"] == "true" || values["client_secret"] != "" {
		stored["credential_configured"] = "true"
	}
	return stored
}

func matches(resource core.AuthResource, search string) bool {
	if search == "" {
		return true
	}
	if strings.Contains(strings.ToLower(resource.ID), strings.ToLower(search)) {
		return true
	}
	for key, value := range resource.Values {
		if key != "client_secret" && strings.Contains(strings.ToLower(value), strings.ToLower(search)) {
			return true
		}
	}
	return false
}

func appendAudit(account *core.Account, p core.Principal, action, target string, now time.Time) {
	event := core.AuditEvent{
		ID: core.NewID("evt"), OrganizationID: p.OrganizationID, ProjectID: p.ProjectID,
		Actor: account.User.Email, Action: action, Target: target, TargetType: "authentication console",
		Risk: "low", Source: "dashboard demo", OccurredAt: now,
	}
	account.AuditEvents = append(account.AuditEvents, event)
	org, exists := account.Organizations[p.OrganizationID]
	if !exists {
		return
	}
	project, exists := org.Projects[p.ProjectID]
	if !exists || project.OrganizationID != org.ID {
		return
	}
	project.AuthInstance.AuditEvents = append(project.AuthInstance.AuditEvents, event)
	org.Projects[project.ID] = project
	account.Organizations[org.ID] = org
}

func redirect(w http.ResponseWriter, r *http.Request, key, status, value string) {
	query := url.Values{status: {value}}
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
