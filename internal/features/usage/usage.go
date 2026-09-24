// Package usage owns provider-backed usage reporting and scoped spending controls.
package usage

import (
	"context"
	"embed"
	"errors"
	"fmt"
	"io/fs"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

//go:embed templates/*.html
var featureTemplates embed.FS

var amountPattern = regexp.MustCompile(`^\d+(?:\.\d{1,2})?$`)

type databaseRow struct {
	ID        string
	Name      string
	Region    string
	SpendCap  string
	Effective string
	Status    string
	Documents int
}

type projectRow struct {
	ID            string
	Name          string
	Region        string
	DatabaseCount int
	SpendCap      string
	Effective     string
}

type invoiceRow struct {
	Number    string
	Status    string
	Amount    string
	CreatedAt time.Time
}

type pageData struct {
	Tab                string
	Projects           []projectRow
	Databases          []databaseRow
	SelectedProject    string
	SelectedDatabase   string
	OrganizationCap    string
	PaidExcess         bool
	EffectiveCap       string
	PlanName           string
	BillingStatus      string
	BillingRenewal     time.Time
	Invoices           []invoiceRow
	Requests           int64
	Reads              int64
	Writes             int64
	StorageByteSeconds int64
	UsageSource        string
	UsageError         string
	Saved              string
	CanManage          bool
	InvoiceCount       int
	ActiveDatabases    int
	RegionCount        int
}

// Mount registers the usage page and spending controls.
func Mount(mux *http.ServeMux, deps core.Deps) {
	templates, err := fs.Sub(featureTemplates, "templates")
	if err != nil {
		panic(err)
	}
	if err := deps.Render.Register("usage", templates, "*.html"); err != nil {
		panic(err)
	}

	mux.HandleFunc("GET /usage", func(w http.ResponseWriter, r *http.Request) {
		p, account, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		org, exists := account.Organizations[p.OrganizationID]
		if !exists {
			http.Error(w, "selected organization is unavailable", http.StatusForbidden)
			return
		}
		projectID := r.URL.Query().Get("project")
		project, valid := org.Projects[projectID]
		if projectID == "" || !valid || project.OrganizationID != org.ID {
			projectID = p.ProjectID
			project = org.Projects[projectID]
		}
		if project.ID == "" {
			http.Error(w, "selected project is unavailable", http.StatusNotFound)
			return
		}
		tab := r.URL.Query().Get("tab")
		if !slices.Contains([]string{"overview", "billing", "settings"}, tab) {
			tab = "overview"
		}
		databaseFilter := r.URL.Query().Get("database")
		if _, exists := project.Databases[databaseFilter]; !exists {
			databaseFilter = ""
		}
		data := buildPage(r.Context(), deps, p, org, project, tab, databaseFilter)
		data.Saved = r.URL.Query().Get("saved")
		page := core.Page{Template: "usage", Layout: "shell", Title: "Usage", Principal: p, Data: data}
		if deps.CSRF != nil {
			page.CSRFToken = deps.CSRF.Token(w, r)
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render usage", http.StatusInternalServerError)
		}
	})

	mux.HandleFunc("POST /api/v1/dashboard/usage/spending-controls", func(w http.ResponseWriter, r *http.Request) {
		if deps.CSRF == nil || deps.CSRF.Verify(r) != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
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
		if !canManage(org, p.UserID) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		organizationCap, hasOrganizationCap, valid := formCap(r.Form, "spendCap")
		if !valid {
			redirect(w, r, "invalid")
			return
		}
		projectCaps, valid := capsFromForm(r.Form, "projCap_")
		if !valid {
			redirect(w, r, "invalid")
			return
		}
		databaseCaps, valid := capsFromForm(r.Form, "dbCap_")
		if !valid {
			redirect(w, r, "invalid")
			return
		}
		err := deps.Store.UpdateAs(p, func(current *core.Account) error {
			org, err := current.OrganizationFor(p, p.OrganizationID)
			if err != nil {
				return err
			}
			if !canManage(org, p.UserID) {
				return core.ErrForbidden
			}
			if hasOrganizationCap {
				org.Billing.SpendCapCents = organizationCap
				org.Billing.PaidExcessEnabled = r.FormValue("paidExcess") == "true" || r.FormValue("paidExcess") == "on"
			}
			for projectID, cap := range projectCaps {
				project, exists := org.Projects[projectID]
				if !exists {
					return core.ErrNotFound
				}
				project.SpendCapCents = cap
				org.Projects[projectID] = project
			}
			for databaseID, cap := range databaseCaps {
				found := false
				for projectID, project := range org.Projects {
					database, exists := project.Databases[databaseID]
					if !exists {
						continue
					}
					database.SpendCapCents = cap
					project.Databases[databaseID] = database
					org.Projects[projectID] = project
					found = true
					break
				}
				if !found {
					return core.ErrNotFound
				}
			}
			current.Organizations[org.ID] = org
			if hasOrganizationCap || len(projectCaps) > 0 || len(databaseCaps) > 0 {
				record(current, p, "usage.spending_controls_updated", org.Name)
			}
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirect(w, r, "ok")
	})
}

func buildPage(ctx context.Context, deps core.Deps, p core.Principal, org core.Organization, project core.Project, tab, databaseFilter string) pageData {
	projects := make([]projectRow, 0, len(org.Projects))
	selectedDatabases := make([]databaseRow, 0, len(project.Databases))
	regions := make(map[string]struct{})
	for _, item := range org.Projects {
		row := projectRow{ID: item.ID, Name: item.Name, Region: item.Region, DatabaseCount: len(item.Databases), SpendCap: dollars(item.SpendCapCents)}
		row.Effective = effectiveCap(org.Billing.SpendCapCents, item.SpendCapCents)
		projects = append(projects, row)
	}
	for _, database := range project.Databases {
		if database.Region != "" {
			regions[database.Region] = struct{}{}
		}
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		selectedDatabases = append(selectedDatabases, databaseRow{
			ID: database.ID, Name: database.Name, Region: database.Region,
			SpendCap: dollars(database.SpendCapCents), Effective: effectiveCap(org.Billing.SpendCapCents, project.SpendCapCents, database.SpendCapCents),
			Status: database.Status, Documents: documentCount(database),
		})
	}
	slices.SortFunc(projects, func(a, b projectRow) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(selectedDatabases, func(a, b databaseRow) int { return strings.Compare(a.Name, b.Name) })
	planName := "No billing plan"
	if plan, ok := deps.Store.Plan(org.PlanID); ok {
		planName = plan.Name
	}
	invoices := make([]invoiceRow, 0, len(org.Billing.Invoices))
	for _, invoice := range org.Billing.Invoices {
		invoices = append(invoices, invoiceRow{
			Number: invoice.Number, Status: invoice.Status,
			Amount:    fmt.Sprintf("%s %d.%02d", strings.ToUpper(invoice.Currency), invoice.AmountCents/100, invoice.AmountCents%100),
			CreatedAt: invoice.CreatedAt,
		})
	}
	slices.SortFunc(invoices, func(a, b invoiceRow) int { return b.CreatedAt.Compare(a.CreatedAt) })
	data := pageData{
		Tab: tab, Projects: projects, Databases: selectedDatabases, SelectedProject: project.ID,
		SelectedDatabase: databaseFilter, OrganizationCap: dollars(org.Billing.SpendCapCents),
		PaidExcess: org.Billing.PaidExcessEnabled, EffectiveCap: effectiveCap(org.Billing.SpendCapCents, project.SpendCapCents),
		PlanName: planName, BillingStatus: org.BillingStatus, BillingRenewal: org.BillingRenewal,
		Invoices: invoices, InvoiceCount: len(invoices),
		ActiveDatabases: len(project.Databases), RegionCount: len(regions), CanManage: canManage(org, p.UserID),
	}
	if deps.Demo {
		data.UsageSource = "Sample dashboard data; these values are not provider-metered usage."
		data.Writes = int64(localDocuments(project))
		return data
	}
	if deps.SDK == nil {
		data.UsageSource = "Live usage provider is not configured."
		data.UsageError = "Usage totals are unavailable until the ts-go provider is connected."
		return data
	}
	client, err := deps.SDK.ForScope(p.OrganizationID, project.ID)
	if err != nil {
		data.UsageSource = "Live usage provider could not be reached."
		data.UsageError = "Usage totals are unavailable for this project."
		return data
	}
	data.UsageSource = "Live usage facts from TerraScale."
	for _, database := range project.Databases {
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		usage, err := client.Usage.GetDatabaseUsage(ctx, database.Name)
		if err != nil {
			data.UsageError = "Some database usage could not be loaded."
			continue
		}
		for _, meter := range usage.Meters {
			value, err := strconv.ParseInt(meter.Units, 10, 64)
			if err != nil || value < 0 {
				data.UsageError = "The provider returned an invalid usage meter."
				continue
			}
			switch meter.Meter {
			case sdk.MeterDocumentWriteUnits:
				if !addUsage(&data.Writes, value) {
					data.UsageError = "The provider reported more writes than this page can display."
				}
			case sdk.MeterStorageByteSeconds:
				if !addUsage(&data.StorageByteSeconds, value) {
					data.UsageError = "The provider reported more storage activity than this page can display."
				}
			}
		}
		const maxInt64 = int64(^uint64(0) >> 1)
		if usage.Reads.TotalReads > uint64(maxInt64-data.Reads) {
			data.UsageError = "The provider reported more reads than this page can display."
		} else {
			data.Reads += int64(usage.Reads.TotalReads)
		}
	}
	if !addUsage(&data.Requests, data.Reads) || !addUsage(&data.Requests, data.Writes) {
		data.Requests = int64(^uint64(0) >> 1)
		data.UsageError = "The provider reported more operations than this page can display."
	}
	return data
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
	if org.OwnerUserID == userID {
		return true
	}
	member, ok := org.Members[userID]
	return ok && member.Status == "active" && (member.Role == "owner" || member.Role == "admin")
}

func documentCount(database core.Database) int {
	count := 0
	for _, collection := range database.Documents {
		count += len(collection)
	}
	return count
}

func localDocuments(project core.Project) int {
	count := 0
	for _, database := range project.Databases {
		count += documentCount(database)
	}
	return count
}

func formCap(values url.Values, key string) (int64, bool, bool) {
	value, exists := values[key]
	if !exists {
		return 0, false, true
	}
	if len(value) != 1 {
		return 0, true, false
	}
	amount := strings.TrimSpace(value[0])
	if amount == "" {
		return 0, true, true
	}
	cents, valid := parseCents(amount)
	return cents, true, valid
}

func capsFromForm(values url.Values, prefix string) (map[string]int64, bool) {
	caps := make(map[string]int64)
	for key, items := range values {
		if !strings.HasPrefix(key, prefix) {
			continue
		}
		id := strings.TrimPrefix(key, prefix)
		if id == "" || len(items) != 1 {
			return nil, false
		}
		value := strings.TrimSpace(items[0])
		if value == "" {
			caps[id] = 0
			continue
		}
		cents, valid := parseCents(value)
		if !valid {
			return nil, false
		}
		caps[id] = cents
	}
	return caps, true
}

func addUsage(total *int64, value int64) bool {
	const maxInt64 = int64(^uint64(0) >> 1)
	if value < 0 || *total > maxInt64-value {
		return false
	}
	*total += value
	return true
}

func parseCents(value string) (int64, bool) {
	if !amountPattern.MatchString(value) {
		return 0, false
	}
	parts := strings.SplitN(value, ".", 2)
	whole, err := strconv.ParseInt(parts[0], 10, 64)
	if err != nil {
		return 0, false
	}
	cents := int64(0)
	if len(parts) == 2 {
		fraction := parts[1]
		if len(fraction) == 1 {
			fraction += "0"
		}
		cents, err = strconv.ParseInt(fraction, 10, 64)
		if err != nil {
			return 0, false
		}
	}
	const maxInt64 = int64(^uint64(0) >> 1)
	if whole > (maxInt64-cents)/100 {
		return 0, false
	}
	return whole*100 + cents, true
}

func dollars(cents int64) string {
	if cents <= 0 {
		return ""
	}
	return fmt.Sprintf("%d.%02d", cents/100, cents%100)
}

func effectiveCap(caps ...int64) string {
	var smallest int64
	for _, cap := range caps {
		if cap > 0 && (smallest == 0 || cap < smallest) {
			smallest = cap
		}
	}
	if smallest == 0 {
		return "No limit"
	}
	return "$" + dollars(smallest)
}

func record(account *core.Account, p core.Principal, action, target string) {
	account.AuditEvents = append(account.AuditEvents, core.AuditEvent{
		ID: core.NewID("evt"), OrganizationID: p.OrganizationID, ProjectID: p.ProjectID,
		Actor: account.User.Email, Action: action, Target: target, TargetType: "usage control",
		Risk: "low", Source: "dashboard", OccurredAt: time.Now().UTC(),
	})
}

func redirect(w http.ResponseWriter, r *http.Request, saved string) {
	query := url.Values{"tab": {"settings"}, "saved": {saved}}
	http.Redirect(w, r, "/usage?"+query.Encode(), http.StatusSeeOther)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "authentication required", http.StatusUnauthorized)
	case errors.Is(err, core.ErrForbidden):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
