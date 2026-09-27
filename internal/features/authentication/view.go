package authentication

import (
	"cmp"
	"encoding/json/v2"
	"fmt"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/icons"
)

const pageSize = 10

type pageData struct {
	Section      section
	Slug         string
	InstanceName string
	Instance     core.AuthInstance
	Settings     core.AuthSettings
	CanManage    bool
	Saved        bool
	Error        string

	Search    string
	Rows      []resourceRow
	Page      int
	PageCount int
	Total     int

	// Choices for organization and user selects.
	Organizations []option
	Users         []option
	// Counts holds the live (not revoked) records of each section.
	Counts      map[string]int
	Blocked     int
	Connections []resourceRow
	Clients     []resourceRow
	Templates   []resourceRow
	Events      []eventRow
	Checklist   []checkStep
}

type resourceRow struct {
	ID       string
	Name     string
	Subtitle string
	Initials string
	Icon     func(int) templ.Component
	Tone     string
	Chips    []string
	Status   string
	Created  time.Time
	Fields   []fieldValue
	Actions  []rowAction
	Callback string
	Values   map[string]string
}

type fieldValue struct {
	Field field
	Value string
}

type rowAction struct {
	Key   string
	Label string
	Icon  func(int) templ.Component
}

type eventRow struct {
	Action string
	Target string
	Actor  string
	At     time.Time
}

type checkStep struct {
	Title  string
	Detail string
	URL    string
	Action string
	Tone   string
	Icon   func(int) templ.Component
	Done   bool
}

// buildPage assembles everything a section needs from the project instance.
func buildPage(current section, project core.Project, manage bool, search, rawPage string) pageData {
	instance := project.AuthInstance
	data := pageData{
		Section: current, Slug: cmp.Or(project.Slug, project.ID), InstanceName: cmp.Or(instance.Name, project.Name),
		Instance: instance, Settings: effectiveSettings(instance), CanManage: manage, Search: search,
		Page: 1, PageCount: 1, Counts: make(map[string]int),
	}
	names := recordNames(instance)
	for _, resource := range instance.Resources {
		if resource.Status != "Revoked" {
			data.Counts[resource.Section]++
		}
		switch resource.Section {
		case "organizations":
			data.Organizations = append(data.Organizations, option{resource.ID, names[resource.ID]})
		case "users":
			data.Users = append(data.Users, option{resource.ID, names[resource.ID]})
		}
		if resource.Section == "users" && resource.Status == "Blocked" {
			data.Blocked++
		}
	}
	byLabel := func(a, b option) int { return strings.Compare(strings.ToLower(a.Label), strings.ToLower(b.Label)) }
	slices.SortFunc(data.Organizations, byLabel)
	slices.SortFunc(data.Users, byLabel)

	rows := func(key string, keep func(core.AuthResource) bool) []resourceRow {
		section, _ := findSection(key)
		result := make([]resourceRow, 0)
		for _, resource := range instance.Resources {
			if resource.Section == key && keep(resource) {
				result = append(result, describe(section, resource, names, data.Slug))
			}
		}
		slices.SortFunc(result, func(a, b resourceRow) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
		return result
	}
	all := func(core.AuthResource) bool { return true }

	switch current.Key {
	case "overview", "branding":
		data.Connections = rows("connections", func(r core.AuthResource) bool { return r.Status != "Disabled" })
		data.Events = events(instance, 6)
		data.Checklist = checklist(data)
	case "quickstart":
		data.Clients = rows("clients", all)
		data.Templates = rows("templates", all)
	case "sessions":
		data.Templates = rows("templates", all)
	case "audit":
		data.Events = events(instance, 0)
	default:
		if !current.lists() {
			break
		}
		matching := rows(current.Key, func(r core.AuthResource) bool { return matches(r, names, search) })
		data.Total = len(matching)
		data.PageCount = max(1, (len(matching)+pageSize-1)/pageSize)
		data.Page = core.ClampPage(rawPage, data.PageCount)
		start := (data.Page - 1) * pageSize
		data.Rows = matching[start:min(start+pageSize, len(matching))]
	}
	return data
}

// describe turns a stored record into its table row.
func describe(current section, resource core.AuthResource, names map[string]string, slug string) resourceRow {
	v := resource.Values
	row := resourceRow{
		ID: resource.ID, Name: names[resource.ID], Status: cmp.Or(resource.Status, "Active"),
		Created: resource.CreatedAt, Icon: current.Icon, Tone: current.Tone, Values: v,
	}
	fields := current.Fields
	switch current.Key {
	case "users":
		row.Initials = initials(row.Name)
		row.Subtitle = v["email"]
		if v["username"] != "" {
			row.Chips = append(row.Chips, "@"+v["username"])
		}
		action := rowAction{Key: "block", Label: "Block user", Icon: icons.Ban}
		if row.Status == "Blocked" {
			action = rowAction{Key: "block", Label: "Unblock user", Icon: icons.CircleCheck}
		}
		row.Actions = append(row.Actions, action)
	case "organizations":
		row.Initials = initials(row.Name)
		row.Subtitle = v["slug"]
	case "memberships":
		row.Name = cmp.Or(names[v["user"]], v["user"])
		row.Initials = initials(row.Name)
		row.Subtitle = names[v["organization"]]
		row.Chips = append(row.Chips, v["role"])
	case "invitations":
		row.Name = v["email"]
		row.Initials = initials(row.Name)
		row.Subtitle = names[v["organization"]]
		row.Chips = append(row.Chips, v["role"])
	case "domains":
		row.Subtitle = names[v["organization"]]
		row.Chips = append(row.Chips, "Verified email enrollment")
	case "connections":
		found, _ := findProvider(v["protocol"])
		row.Icon, row.Tone, fields = found.Icon, found.Tone, found.Fields
		row.Subtitle = v["issuer"]
		row.Chips = append(row.Chips, cmp.Or(v["protocol"], "OIDC"), cmp.Or(found.Kind, "Enterprise"))
		if org := names[v["organization"]]; org != "" {
			row.Chips = append(row.Chips, org)
		}
		row.Callback = callbackURL(slug, resource.ID, v["protocol"])
		action := rowAction{Key: "toggle", Label: "Disable connection", Icon: icons.Power}
		if row.Status == "Disabled" {
			action = rowAction{Key: "toggle", Label: "Enable connection", Icon: icons.Power}
		}
		row.Actions = append(row.Actions, action)
	case "directories":
		row.Name = cmp.Or(names[v["organization"]], "Instance-wide directory")
		row.Subtitle = issuerOrigin + "/scim/" + resource.ID
		if rotated, err := time.Parse(time.RFC3339, v["token_rotated_at"]); err == nil {
			row.Chips = append(row.Chips, "Token rotated "+rotated.Format("Jan 2, 2006"))
		} else {
			row.Chips = append(row.Chips, "Bearer token")
		}
		row.Actions = append(row.Actions, rowAction{Key: "rotate", Label: "Rotate token", Icon: icons.RefreshCw})
	case "clients":
		row.Subtitle = resource.ID
		label := "Confidential"
		if v["type"] == "none" {
			label = "Public · PKCE"
		}
		uris := strings.Fields(v["redirect_uris"])
		row.Chips = append(row.Chips, label, plural(len(uris), "redirect URI", "redirect URIs"))
	case "templates":
		row.Subtitle = "POST /v1/" + slug + "/jwt-templates/" + v["name"] + "/tokens"
		row.Chips = append(row.Chips, v["lifetime_seconds"]+"s lifetime", plural(claimCount(v["claims"]), "claim", "claims"))
	case "keys":
		row.Subtitle = "sk_ · " + resource.ID
		row.Chips = strings.Split(v["scopes"], ",")
	case "webhooks":
		events := strings.Split(v["events"], ",")
		row.Subtitle = "Signed with an HMAC secret · retries with dead-letter"
		row.Chips = append(row.Chips, events[:min(3, len(events))]...)
		if len(events) > 3 {
			row.Chips = append(row.Chips, fmt.Sprintf("+%d more", len(events)-3))
		}
	}
	if current.Editable {
		for _, item := range fields {
			if !item.Immutable {
				value := v[item.Key]
				if item.Type == typeSecret {
					value, item.Required = "", false
					item.Placeholder, item.Hint = "Unchanged", "Leave empty to keep the current secret."
				}
				row.Fields = append(row.Fields, fieldValue{Field: item, Value: value})
			}
		}
	}
	return row
}

// recordNames maps every record ID to its stored label.
func recordNames(instance core.AuthInstance) map[string]string {
	names := make(map[string]string, len(instance.Resources))
	for _, resource := range instance.Resources {
		names[resource.ID] = recordName(resource)
	}
	return names
}

// recordName is the stored label of a record; describe refines the rows
// whose label comes from the records they reference.
func recordName(resource core.AuthResource) string {
	v := resource.Values
	return cmp.Or(v["name"], v["email"], v["domain"], v["url"], resource.ID)
}

func matches(resource core.AuthResource, names map[string]string, search string) bool {
	if search == "" {
		return true
	}
	needle := strings.ToLower(search)
	haystack := []string{resource.ID, names[resource.ID], names[resource.Values["user"]], names[resource.Values["organization"]]}
	for _, value := range resource.Values {
		haystack = append(haystack, value)
	}
	return slices.ContainsFunc(haystack, func(value string) bool { return strings.Contains(strings.ToLower(value), needle) })
}

func events(instance core.AuthInstance, limit int) []eventRow {
	rows := make([]eventRow, 0, len(instance.AuditEvents))
	for _, item := range instance.AuditEvents {
		if strings.HasPrefix(item.Action, "auth.") {
			rows = append(rows, eventRow{Action: item.Action, Target: item.Target, Actor: item.Actor, At: item.OccurredAt})
		}
	}
	slices.SortFunc(rows, func(a, b eventRow) int { return b.At.Compare(a.At) })
	if limit > 0 && len(rows) > limit {
		rows = rows[:limit]
	}
	return rows
}

func checklist(data pageData) []checkStep {
	instance := data.Instance
	return []checkStep{
		{
			Title: "Choose sign-in methods", Detail: "Pick the identifiers and factors users sign in with.",
			URL: "/auth/sign-in", Action: "Configure", Tone: "blue", Icon: icons.LockKeyhole, Done: !instance.Settings.UpdatedAt.IsZero(),
		},
		{
			Title: "Brand the hosted pages", Detail: "Set the name, logo, and color users see when they sign in.",
			URL: "/auth/branding", Action: "Customize", Tone: "pink", Icon: icons.Wand2,
			Done: instance.Name != "" || instance.LogoURL != "" || instance.ThemeColor != "",
		},
		{
			Title: "Register an OAuth application", Detail: "Get a client ID for the authorization code flow with PKCE.",
			URL: "/auth/clients", Action: "Register", Tone: "violet", Icon: icons.Boxes, Done: data.Counts["clients"] > 0,
		},
		{
			Title: "Add an SSO connection", Detail: "Offer Google, Microsoft, GitHub, OIDC, or SAML sign-in.",
			URL: "/auth/connections", Action: "Connect", Tone: "cyan", Icon: icons.GlobeLock, Done: data.Counts["connections"] > 0,
		},
		{
			Title: "Create your first user", Detail: "Add someone by email to try the hosted sign-in flow.",
			URL: "/auth/users", Action: "Add user", Tone: "emerald", Icon: icons.UserPlus, Done: data.Counts["users"] > 0,
		},
		{
			Title: "Subscribe to events", Detail: "Receive signed webhooks when users and organizations change.",
			URL: "/auth/webhooks", Action: "Add endpoint", Tone: "orange", Icon: icons.Webhook, Done: data.Counts["webhooks"] > 0,
		},
	}
}

func (d pageData) completedSteps() int {
	done := 0
	for _, step := range d.Checklist {
		if step.Done {
			done++
		}
	}
	return done
}

func (d pageData) issuer() string { return issuerOrigin + "/" + d.Slug }

func (d pageData) hosted(path string) string { return issuerOrigin + "/ui/" + d.Slug + path }

func (d pageData) pageURL(page int) string {
	query := "?page=" + strconv.Itoa(page)
	if d.Search != "" {
		query += "&q=" + url.QueryEscape(d.Search)
	}
	return "/auth/" + d.Section.Key + query
}

// callbackURL is the redirect URL to register with the upstream provider.
func callbackURL(slug, id, protocol string) string {
	switch protocol {
	case "GitHub":
		return issuerOrigin + "/ui/" + slug + "/connections/" + id + "/github/callback"
	case "SAML":
		return issuerOrigin + "/" + slug + "/connections/" + id + "/saml/acs"
	}
	return issuerOrigin + "/ui/" + slug + "/connections/" + id + "/oidc/callback"
}

func initials(name string) string {
	name, _, _ = strings.Cut(name, "@")
	letters := make([]rune, 0, 2)
	for word := range strings.FieldsFuncSeq(name, func(r rune) bool { return !unicode.IsLetter(r) && !unicode.IsDigit(r) }) {
		letters = append(letters, unicode.ToUpper([]rune(word)[0]))
		if len(letters) == 2 {
			break
		}
	}
	if len(letters) == 0 {
		return "?"
	}
	return string(letters)
}

func plural(count int, one, many string) string {
	if count == 1 {
		return "1 " + one
	}
	return strconv.Itoa(count) + " " + many
}

func claimCount(claims string) int {
	var object map[string]any
	if json.Unmarshal([]byte(claims), &object) != nil {
		return 0
	}
	return len(object)
}
