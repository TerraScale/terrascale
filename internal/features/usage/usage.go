// Package usage owns provider-backed usage reporting and scoped spending controls.
package usage

import (
	"context"
	"errors"
	"fmt"
	"math"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/demovisuals"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/ui/charts"
	sdk "terrascale.tech/sdk"
)

var amountPattern = regexp.MustCompile(`^\d+(?:\.\d{1,2})?$`)

type databaseRow struct {
	ID           string
	Name         string
	Region       string
	SpendCap     string
	Effective    string
	Status       string
	Documents    int
	StoredBytes  int64
	StoredSize   string
	SharePercent int
}

type databaseOption struct {
	ID   string
	Name string
}

type projectRow struct {
	ID            string
	Name          string
	Region        string
	DatabaseCount int
	SpendCap      string
	Effective     string
	Selected      bool
	StoredBytes   int64
	StoredSize    string
	SharePercent  int
}

type sampleGauge struct {
	Name     string
	Used     string
	Capacity string
	Percent  int
	Ring     charts.RingData
}

type historyTotal struct {
	Name  string
	Value int64
	Class string
}

type billingSummary struct {
	NextInvoice       string
	NextDetail        string
	Period            string
	PeriodDetail      string
	PeriodPercent     int
	HasPeriod         bool
	Outstanding       string
	OutstandingDetail string
}

type invoiceRow struct {
	Number      string
	Status      string
	Amount      string
	CreatedAt   time.Time
	PeriodStart time.Time
	PeriodEnd   time.Time
	HostedURL   string
	PDFURL      string
}

type invoiceFilterOption struct {
	Value string
	Label string
}

type invoicePage struct {
	Rows          []invoiceRow
	Status        string
	Issued        string
	StatusOptions []invoiceFilterOption
	IssuedOptions []invoiceFilterOption
	Page          int
	PageCount     int
	Total         int
	First         int
}

const invoicePageSize = 10

type pageData struct {
	Tab                   string
	Projects              []projectRow
	DatabaseOptions       []databaseOption
	Databases             []databaseRow
	SelectedProject       string
	SelectedDatabase      string
	ExplicitProjectFilter bool
	OrganizationCap       string
	PaidExcess            bool
	EffectiveCap          string
	PlanName              string
	BillingPlanKey        string
	BillingInterval       string
	BillingPrice          string
	BillingNotice         string
	BillingSubscription   bool
	Personal              bool
	Demo                  bool
	ScheduledPlan         string
	PendingPlan           string
	ScheduledAt           time.Time
	BillingPlans          []billingPlanChoice
	Review                billingPlanReview
	PlanStorageAllowance  string
	PlanRequestAllowance  string
	PlanUsageNote         string
	HistoryRange          string
	HistoryRangeLabel     string
	HistoryChart          charts.LineData
	HistoryTotals         []historyTotal
	SampleGauges          []sampleGauge
	SampleHistory         bool
	BillingStatus         string
	BillingRenewal        time.Time
	BillingSummary        billingSummary
	Invoices              []invoiceRow
	InvoicePage           invoicePage
	Requests              int64
	Reads                 int64
	Writes                int64
	StorageByteSeconds    int64
	UsageSource           string
	UsageError            string
	UsageAvailable        bool
	Saved                 string
	CanManage             bool
	CanManageBilling      bool
	InvoiceCount          int
	ActiveDatabases       int
	RegionCount           int
}

type billingPlanChoice struct {
	Key, Name, MonthlyPrice, AnnualPrice, Storage, Requests, Databases, Members string
}

type billingPlanReview struct {
	Open, Ready, Immediate                                                                     bool
	Key, Name, Interval, Price, Effect, Estimate, Storage, Requests, Databases, Members, Error string
	// CanUndo offers to undo the scheduled change that blocks this one.
	CanUndo bool
	// Warnings name the allowances the organization already exceeds on the
	// target plan.
	Warnings []limitWarning
}

// limitWarning is one allowance the organization exceeds on a plan and what
// the plan's limit then blocks.
type limitWarning struct {
	Title, Detail string
}

// Mount registers the usage page and spending controls. billing is nil in
// demo mode.
func Mount(mux *http.ServeMux, deps core.Deps, billing *payments.Stripe) {
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
		tab := r.URL.Query().Get("tab")
		if !slices.Contains([]string{"overview", "billing", "settings"}, tab) {
			tab = "overview"
		}
		projectID := r.URL.Query().Get("project")
		project, valid := org.Projects[projectID]
		if projectID == "" || !valid || project.OrganizationID != org.ID {
			projectID = p.ProjectID
			project = org.Projects[projectID]
		}
		if project.ID == "" && tab != "billing" {
			// Without a project, Overview leads to the project setup.
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		databaseFilter := r.URL.Query().Get("database")
		if _, exists := project.Databases[databaseFilter]; !exists {
			databaseFilter = ""
		}
		data := buildPage(r.Context(), deps, p, org, project, tab, databaseFilter)
		data.ExplicitProjectFilter = r.URL.Query().Get("project") == project.ID
		data.HistoryRange = demovisuals.NormalizeRange(r.URL.Query().Get("range"))
		data.HistoryRangeLabel = demovisuals.RangeLabel(data.HistoryRange)
		if demovisuals.IsSeededMarioAccount(deps.Demo, account, org) {
			addMarioSampleVisuals(&data)
		}
		now := time.Now().UTC()
		data.InvoicePage = makeInvoicePage(
			data.Invoices,
			r.URL.Query().Get("status"),
			r.URL.Query().Get("issued"),
			r.URL.Query().Get("page"),
			now,
		)
		data.CanManageBilling = org.OwnerUserID == p.UserID
		data.Personal = org.Kind == "personal"
		data.Demo = deps.Demo
		data.BillingPlanKey = org.Billing.PlanKey
		data.BillingInterval = org.Billing.Interval
		data.BillingSubscription = org.Billing.SubscriptionID != "" && org.Billing.Status != "canceled" && org.Billing.Status != "incomplete_expired"
		data.ScheduledPlan = planName(deps.Store, org.Billing.ScheduledPlanKey)
		data.PendingPlan = planName(deps.Store, org.Billing.PendingPlanKey)
		data.ScheduledAt = org.Billing.ScheduledAt
		if data.BillingPlanKey == "" && org.PlanID == "plan_free" {
			data.BillingPlanKey = "free"
		}
		for _, key := range []string{"starter", "pro", "business"} {
			plan, exists := deps.Store.Plan(key)
			if !exists || !plan.Visible || plan.PriceCents <= 0 {
				continue
			}
			data.BillingPlans = append(data.BillingPlans, billingPlanChoice{
				Key: key, Name: plan.Name, MonthlyPrice: priceUSD(plan.PriceCents), AnnualPrice: priceUSD(plan.AnnualPriceCents),
				Storage: plan.StorageAllowance(), Requests: plan.OperationsAllowance(), Databases: plan.DatabaseAllowance(), Members: plan.MemberAllowance(),
			})
		}
		if plan, exists := deps.Store.Plan(org.PlanID); exists {
			cents := plan.PriceCents
			if org.Billing.Interval == "annual" {
				cents = plan.AnnualPriceCents
			}
			if cents > 0 {
				data.BillingPrice = priceUSD(cents)
			}
		}
		if tab == "billing" && data.CanManageBilling && r.URL.Query().Get("review") == "1" {
			data.Review = reviewBillingPlan(r, deps, billing, org, data)
		}
		data.Saved = r.URL.Query().Get("saved")
		if tab == "billing" {
			switch r.URL.Query().Get("billing") {
			case "pending":
				data.BillingNotice = "Waiting for Stripe to confirm the plan change and payment."
			case "scheduled":
				data.BillingNotice = "Your plan change is scheduled for renewal."
			case "changed":
				data.BillingNotice = "Your demo plan is now active."
			case "undone":
				data.BillingNotice = "The scheduled change was undone."
			}
		}
		page := core.Page{
			Layout: "shell", Title: "Usage", Principal: p,
			View: func(view core.Page) templ.Component {
				return usageView(data, view.CSRFToken)
			},
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render usage", http.StatusInternalServerError)
		}
	})

	mux.HandleFunc("POST /api/v1/dashboard/usage/spending-controls", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
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

func priceUSD(cents int64) string {
	if cents < 0 {
		return "-" + priceUSD(-cents)
	}
	return fmt.Sprintf("US$ %d.%02d", cents/100, cents%100)
}

func reviewBillingPlan(r *http.Request, deps core.Deps, billing *payments.Stripe, org core.Organization, data pageData) billingPlanReview {
	key, interval := strings.ToLower(r.URL.Query().Get("plan")), strings.ToLower(r.URL.Query().Get("interval"))
	review := billingPlanReview{Open: true, Key: key, Interval: interval}
	// Billing applies one change at a time and needs a healthy subscription,
	// so these states would reject any confirmation.
	if data.BillingSubscription {
		switch {
		case data.PendingPlan != "":
			review.Error = "Stripe is still confirming the change to " + data.PendingPlan + ". You can change plans again once it’s confirmed."
			return review
		case data.ScheduledPlan != "":
			review.Error = "A change to " + data.ScheduledPlan + " is scheduled to take effect " + whenScheduled(data.ScheduledAt) + ". Undo it to choose a different plan."
			review.CanUndo = true
			return review
		case org.BillingStatus == "billing_required", !payments.IsEntitledStatus(org.Billing.Status):
			review.Error = "Stripe reports a payment problem with this subscription. Resolve it in Manage payment before changing plans."
			return review
		case org.Billing.CancelAtPeriodEnd:
			// Personal workspaces see this as a scheduled change to Free; an
			// organization's subscription can only be resumed in the portal.
			review.Error = "This subscription ends " + whenScheduled(org.BillingRenewal) + ". Resume it in Manage payment before changing plans."
			return review
		}
	}
	if key == "free" {
		if !data.Personal || !data.BillingSubscription {
			review.Error = "Free is available after a Personal paid subscription."
			return review
		}
		review.Name, review.Price, review.Effect, review.Estimate = "Free", "US$ 0.00", atRenewal(org.BillingRenewal), "No charge today"
		if free, exists := deps.Store.Plan("free"); exists {
			review.Storage, review.Requests, review.Databases, review.Members = free.StorageAllowance(), free.OperationsAllowance(), free.DatabaseAllowance(), free.MemberAllowance()
			review.Warnings = limitWarnings(org, free, time.Now())
		}
		review.Ready = true
		return review
	}
	if interval != "monthly" && interval != "annual" {
		review.Error = "Choose monthly or annual billing."
		return review
	}
	plan, ok := deps.Store.Plan(key)
	if !ok || !plan.Visible || plan.PriceCents <= 0 || interval == "annual" && plan.AnnualPriceCents <= 0 {
		review.Error = "Choose an available plan."
		return review
	}
	review.Name = plan.Name
	review.Price = priceUSD(plan.PriceCents) + " / month"
	if interval == "annual" {
		review.Price = priceUSD(plan.AnnualPriceCents) + " / year"
	}
	review.Storage, review.Requests, review.Databases, review.Members = plan.StorageAllowance(), plan.OperationsAllowance(), plan.DatabaseAllowance(), plan.MemberAllowance()
	review.Warnings = limitWarnings(org, plan, time.Now())
	if !data.BillingSubscription {
		review.Effect = "After Stripe confirms checkout"
		if !org.Billing.TrialUsed && plan.TrialDays > 0 {
			review.Effect = fmt.Sprintf("After Stripe confirms the %d-day trial", plan.TrialDays)
		}
		review.Estimate, review.Ready = "Stripe shows the final amount in Checkout", true
		return review
	}
	if key == data.BillingPlanKey && interval == data.BillingInterval {
		review.Error = "This is your current plan."
		return review
	}
	review.Immediate = payments.PlanRank(key) > payments.PlanRank(data.BillingPlanKey)
	if review.Immediate {
		review.Effect = "After Stripe confirms the change and payment"
		if deps.Demo {
			review.Estimate, review.Ready = "Demo only; no Stripe charge", true
		} else {
			quote, err := billing.PreviewPlanChange(r.Context(), org.Billing.CustomerID, org.Billing.SubscriptionID, key, interval)
			if errors.Is(err, payments.ErrStripeUnavailable) {
				review.Error = "Plan changes are not available yet."
			} else if err != nil {
				review.Error = "Stripe could not calculate the estimated charge. Try again."
			} else {
				review.Estimate = priceUSD(quote.AmountDueCents) + " estimated by Stripe (including " + priceUSD(quote.ProrationCents) + " in prorations)"
				review.Ready = true
			}
		}
	} else {
		review.Effect = atRenewal(org.BillingRenewal)
		review.Estimate, review.Ready = "No charge today", true
	}
	return review
}

// atRenewal says when a change scheduled for the renewal date applies.
func atRenewal(renewal time.Time) string {
	if renewal.IsZero() {
		return "At the next renewal"
	}
	return "At renewal on " + renewal.UTC().Format("Jan 2, 2006")
}

// whenScheduled says when a scheduled change applies. Stripe may not report
// the period end yet, and then the change applies at the next renewal.
func whenScheduled(at time.Time) string {
	if at.IsZero() {
		return "at the next renewal"
	}
	return "on " + at.UTC().Format("Jan 2, 2006")
}

// limitWarnings names each allowance the organization already exceeds on the
// target plan. A plan change never removes resources; the limits only block
// new databases and invitations.
func limitWarnings(org core.Organization, target core.Plan, now time.Time) []limitWarning {
	var warnings []limitWarning
	databases := 0
	for _, project := range org.Projects {
		databases += len(project.Databases)
	}
	if target.MaxDatabases > 0 && databases > target.MaxDatabases {
		warnings = append(warnings, limitWarning{
			Title:  "More databases than " + target.Name + " allows",
			Detail: fmt.Sprintf("%s has %d databases and %s allows %d. Existing databases stay, but new ones can’t be created until it’s under the limit.", org.Name, databases, target.Name, target.MaxDatabases),
		})
	}
	seats := 0
	for _, member := range org.Members {
		if member.Status == "active" {
			seats++
		}
	}
	for _, invitation := range org.Invitations {
		if invitation.Status == "pending" && invitation.ExpiresAt.After(now) {
			seats++
		}
	}
	if target.MaxMembers > 0 && seats > target.MaxMembers {
		warnings = append(warnings, limitWarning{
			Title:  "More members than " + target.Name + " allows",
			Detail: fmt.Sprintf("%s has %d members and pending invitations and %s allows %d. Current members keep access, but new invitations are blocked until it’s under the limit.", org.Name, seats, target.Name, target.MaxMembers),
		})
	}
	return warnings
}

// planName is a plan key's display name, or the key when no plan has it.
func planName(store *core.Store, key string) string {
	if plan, ok := store.Plan(key); ok {
		return plan.Name
	}
	return key
}

func buildPage(ctx context.Context, deps core.Deps, p core.Principal, org core.Organization, project core.Project, tab, databaseFilter string) pageData {
	projects := make([]projectRow, 0, len(org.Projects))
	databaseOptions := make([]databaseOption, 0, len(project.Databases))
	selectedDatabases := make([]databaseRow, 0, len(project.Databases))
	regions := make(map[string]struct{})
	for _, item := range org.Projects {
		region := item.Region
		if strings.TrimSpace(region) == "" {
			region = "Not configured"
		}
		row := projectRow{ID: item.ID, Name: item.Name, Region: region, DatabaseCount: len(item.Databases), SpendCap: dollars(item.SpendCapCents), Selected: item.ID == project.ID}
		for _, database := range item.Databases {
			row.StoredBytes += storedDocumentBytes(database)
		}
		row.StoredSize = formatStoredBytes(row.StoredBytes)
		row.Effective = effectiveCap(org.Billing.SpendCapCents, item.SpendCapCents)
		projects = append(projects, row)
	}
	for _, database := range project.Databases {
		if database.Region != "" {
			regions[database.Region] = struct{}{}
		}
		databaseOptions = append(databaseOptions, databaseOption{ID: database.ID, Name: database.Name})
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		storedBytes := storedDocumentBytes(database)
		selectedDatabases = append(selectedDatabases, databaseRow{
			ID: database.ID, Name: database.Name, Region: database.Region,
			SpendCap: dollars(database.SpendCapCents), Effective: effectiveCap(org.Billing.SpendCapCents, project.SpendCapCents, database.SpendCapCents),
			Status: database.Status, Documents: documentCount(database), StoredBytes: storedBytes, StoredSize: formatStoredBytes(storedBytes),
		})
	}
	var projectStoredBytes, databaseStoredBytes int64
	for _, item := range projects {
		projectStoredBytes += item.StoredBytes
	}
	for i := range projects {
		projects[i].SharePercent = sharePercent(projects[i].StoredBytes, projectStoredBytes)
	}
	for _, item := range selectedDatabases {
		databaseStoredBytes += item.StoredBytes
	}
	for i := range selectedDatabases {
		selectedDatabases[i].SharePercent = sharePercent(selectedDatabases[i].StoredBytes, databaseStoredBytes)
	}
	slices.SortFunc(projects, func(a, b projectRow) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(databaseOptions, func(a, b databaseOption) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(selectedDatabases, func(a, b databaseRow) int { return strings.Compare(a.Name, b.Name) })
	planName, storageAllowance, requestAllowance := "No billing plan", "Not configured", "Not configured"
	if plan, ok := deps.Store.Plan(org.PlanID); ok {
		planName = plan.Name
		storageAllowance = plan.StorageAllowance()
		requestAllowance = plan.OperationsAllowance()
	}
	invoices := make([]invoiceRow, 0, len(org.Billing.Invoices))
	for _, invoice := range org.Billing.Invoices {
		invoices = append(invoices, invoiceRow{
			Number: invoice.Number, Status: invoice.Status,
			Amount:    fmt.Sprintf("%s %d.%02d", strings.ToUpper(invoice.Currency), invoice.AmountCents/100, invoice.AmountCents%100),
			CreatedAt: invoice.CreatedAt, PeriodStart: invoice.PeriodStart, PeriodEnd: invoice.PeriodEnd,
			HostedURL: httpsInvoiceURL(invoice.HostedURL), PDFURL: httpsInvoiceURL(invoice.PDFURL),
		})
	}
	slices.SortStableFunc(invoices, func(a, b invoiceRow) int { return b.CreatedAt.Compare(a.CreatedAt) })
	data := pageData{
		Tab: tab, Projects: projects, DatabaseOptions: databaseOptions, Databases: selectedDatabases, SelectedProject: project.ID,
		SelectedDatabase: databaseFilter, OrganizationCap: dollars(org.Billing.SpendCapCents),
		PaidExcess: org.Billing.PaidExcessEnabled, EffectiveCap: effectiveCap(org.Billing.SpendCapCents, project.SpendCapCents),
		PlanName: planName, PlanStorageAllowance: storageAllowance, PlanRequestAllowance: requestAllowance,
		PlanUsageNote: "Provider totals have no billing-period attribution, so allowance utilization is unavailable.",
		BillingStatus: org.BillingStatus, BillingRenewal: org.BillingRenewal,
		BillingSummary: summarizeBilling(org.Billing.Invoices, time.Now().UTC()),
		Invoices:       invoices, InvoiceCount: len(invoices),
		ActiveDatabases: len(project.Databases), RegionCount: len(regions), CanManage: canManage(org, p.UserID),
	}
	if deps.Demo {
		data.UsageSource = "Sample dashboard data; these values are not provider-metered usage."
		data.PlanUsageNote = "Demo mode has no measured usage feed, so allowance utilization is unavailable."
		data.Writes = int64(localDocuments(project, databaseFilter))
		data.UsageAvailable = true
		return data
	}
	if project.ID == "" {
		return data
	}
	if deps.SDK == nil {
		data.UsageSource = "Live usage provider is not configured."
		data.UsageError = "Usage totals are unavailable until the ts-go provider is connected."
		data.PlanUsageNote = "The usage provider is not configured, so allowance utilization is unavailable."
		return data
	}
	client, err := deps.SDK.ForScope(p.OrganizationID, project.ID)
	if err != nil {
		data.UsageSource = "Live usage provider could not be reached."
		data.UsageError = "Usage totals are unavailable for this project."
		data.PlanUsageNote = "The usage provider could not be reached, so allowance utilization is unavailable."
		return data
	}
	data.UsageSource = "Live usage facts from TerraScale."
	usageFetched := len(project.Databases) == 0
	for _, database := range project.Databases {
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		usage, err := client.Usage.GetDatabaseUsage(ctx, database.Name)
		if err != nil {
			data.UsageError = "Some database usage could not be loaded."
			continue
		}
		usageFetched = true
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
		if usage.Reads.TotalReads > uint64(math.MaxInt64-data.Reads) {
			data.UsageError = "The provider reported more reads than this page can display."
		} else {
			data.Reads += int64(usage.Reads.TotalReads)
		}
	}
	if !addUsage(&data.Requests, data.Reads) || !addUsage(&data.Requests, data.Writes) {
		data.Requests = math.MaxInt64
		data.UsageError = "The provider reported more operations than this page can display."
	}
	data.UsageAvailable = usageFetched
	return data
}

func makeInvoicePage(invoices []invoiceRow, rawStatus, rawIssued, rawPage string, now time.Time) invoicePage {
	statusOptions := []invoiceFilterOption{
		{Value: "paid", Label: "Paid"},
		{Value: "open", Label: "Open"},
		{Value: "void", Label: "Void"},
	}
	status := ""
	for _, option := range statusOptions {
		if option.Value == rawStatus {
			status = rawStatus
			break
		}
	}

	issuedOptions := invoiceIssuedOptions(now)
	issued := ""
	for _, option := range issuedOptions {
		if option.Value == rawIssued {
			issued = rawIssued
			break
		}
	}

	cutoff := time.Time{}
	issuedYear := 0
	switch issued {
	case "3m":
		cutoff = now.AddDate(0, -3, 0)
	case "6m":
		cutoff = now.AddDate(0, -6, 0)
	case "12m":
		cutoff = now.AddDate(0, -12, 0)
	case "":
	default:
		issuedYear, _ = strconv.Atoi(issued)
	}

	filtered := slices.DeleteFunc(slices.Clone(invoices), func(invoice invoiceRow) bool {
		if status != "" && invoice.Status != status {
			return true
		}
		if issued == "" {
			return false
		}
		if issuedYear != 0 {
			return invoice.CreatedAt.UTC().Year() != issuedYear
		}
		return invoice.CreatedAt.Before(cutoff)
	})
	slices.SortStableFunc(filtered, func(a, b invoiceRow) int { return b.CreatedAt.Compare(a.CreatedAt) })

	pageCount := max(1, (len(filtered)+invoicePageSize-1)/invoicePageSize)
	page, err := strconv.Atoi(rawPage)
	if err != nil || page < 1 {
		page = 1
	}
	page = min(page, pageCount)
	start := (page - 1) * invoicePageSize
	end := min(start+invoicePageSize, len(filtered))
	first := 0
	if len(filtered) > 0 {
		first = start + 1
	}
	return invoicePage{
		Rows: filtered[start:end], Status: status, Issued: issued,
		StatusOptions: statusOptions, IssuedOptions: issuedOptions,
		Page: page, PageCount: pageCount, Total: len(filtered), First: first,
	}
}

func invoiceIssuedOptions(now time.Time) []invoiceFilterOption {
	return []invoiceFilterOption{
		{Value: "3m", Label: "Last 3 months"},
		{Value: "6m", Label: "Last 6 months"},
		{Value: "12m", Label: "Last 12 months"},
		{Value: strconv.Itoa(now.Year()), Label: strconv.Itoa(now.Year())},
		{Value: strconv.Itoa(now.Year() - 1), Label: strconv.Itoa(now.Year() - 1)},
	}
}

func (page invoicePage) Href(project string, number int) string {
	query := url.Values{"tab": {"billing"}}
	if project != "" {
		query.Set("project", project)
	}
	if page.Status != "" {
		query.Set("status", page.Status)
	}
	if page.Issued != "" {
		query.Set("issued", page.Issued)
	}
	if number > 1 {
		query.Set("page", strconv.Itoa(number))
	}
	return "/usage?" + query.Encode()
}

func billingTabHref(project string) string {
	return (invoicePage{}).Href(project, 1)
}

func invoiceDate(value time.Time) string {
	if value.IsZero() {
		return "-"
	}
	return value.UTC().Format("Jan 2, 2006")
}

func httpsInvoiceURL(raw string) string {
	parsed, err := url.Parse(raw)
	if err != nil || parsed.Scheme != "https" || parsed.Hostname() == "" || parsed.User != nil {
		return ""
	}
	return parsed.String()
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

func storedDocumentBytes(database core.Database) int64 {
	var total int64
	for _, collection := range database.Documents {
		for _, document := range collection {
			total += int64(len(document))
		}
	}
	return total
}

func formatStoredBytes(value int64) string {
	if value < 1024 {
		return fmt.Sprintf("%d B", value)
	}
	if value < 1024*1024 {
		return fmt.Sprintf("%.1f KiB", float64(value)/1024)
	}
	return fmt.Sprintf("%.1f MiB", float64(value)/(1024*1024))
}

func sharePercent(part, total int64) int {
	if part <= 0 || total <= 0 {
		return 0
	}
	return max(1, int(float64(part)/float64(total)*100+0.5))
}

func addMarioSampleVisuals(data *pageData) {
	values := demovisuals.Workload(data.HistoryRange)
	reads, writes, replication := demovisuals.Operations(values)
	data.SampleHistory = true
	data.HistoryChart = charts.NewLine(
		"Illustrative organization reads, writes, and replication, "+data.HistoryRangeLabel,
		demovisuals.TickLabels(data.HistoryRange), demovisuals.PointLabels(data.HistoryRange, len(values)), 200,
		charts.Series{Name: "Reads", Values: reads},
		charts.Series{Name: "Writes", Values: writes},
		charts.Series{Name: "Replication", Values: replication},
	)
	data.HistoryTotals = []historyTotal{
		{Name: "Reads", Value: demovisuals.Sum(reads), Class: "ts-cat-1"},
		{Name: "Writes", Value: demovisuals.Sum(writes), Class: "ts-cat-2"},
		{Name: "Replication", Value: demovisuals.Sum(replication), Class: "ts-cat-3"},
	}
	storageRing := charts.NewRing("Sample storage", demovisuals.SampleStorageMiB, demovisuals.SampleStorageCapacity)
	writeRing := charts.NewRing("Sample writes", demovisuals.SampleWrites, demovisuals.SampleWriteCapacity)
	data.SampleGauges = []sampleGauge{
		{Name: "Sample storage", Used: "6.5 GiB", Capacity: "8 GiB", Percent: storageRing.Percent, Ring: storageRing},
		{Name: "Sample writes", Used: "8,140,200", Capacity: "5,000,000", Percent: writeRing.Percent, Ring: writeRing},
	}
}

func summarizeBilling(invoices []core.Invoice, now time.Time) billingSummary {
	summary := billingSummary{
		NextInvoice: "Not recorded", NextDetail: "No upcoming invoice is recorded.",
		Period: "Unavailable", PeriodDetail: "No current billing period is recorded.",
		Outstanding: "Not recorded", OutstandingDetail: "No invoices are recorded.",
	}
	var current, upcoming *core.Invoice
	var openCount int
	var outstanding int64
	openCurrency := ""
	mixedCurrency := false
	for i := range invoices {
		invoice := &invoices[i]
		if !invoice.PeriodStart.IsZero() && !invoice.PeriodEnd.IsZero() &&
			!now.Before(invoice.PeriodStart) && now.Before(invoice.PeriodEnd) &&
			(current == nil || invoice.PeriodStart.After(current.PeriodStart)) {
			current = invoice
		}
		if !strings.EqualFold(invoice.Status, "open") {
			continue
		}
		openCount++
		if invoice.AmountCents > 0 {
			outstanding += invoice.AmountCents
		}
		currency := strings.ToUpper(invoice.Currency)
		if openCount == 1 {
			openCurrency = currency
		} else if openCurrency != currency {
			mixedCurrency = true
		}
		if !invoice.CreatedAt.Before(now) && (upcoming == nil || invoice.CreatedAt.Before(upcoming.CreatedAt)) {
			upcoming = invoice
		}
	}
	if current != nil {
		days := max(1, int(current.PeriodEnd.Sub(current.PeriodStart).Hours()/24))
		elapsed := min(days, max(0, int(now.Sub(current.PeriodStart).Hours()/24)))
		summary.Period = current.PeriodStart.UTC().Format("Jan 2") + " – " + current.PeriodEnd.UTC().Format("Jan 2, 2006")
		summary.PeriodDetail = fmt.Sprintf("Day %d of %d · based on a recorded invoice period", elapsed, days)
		summary.PeriodPercent = sharePercent(int64(elapsed), int64(days))
		summary.HasPeriod = true
	}
	if upcoming != nil {
		summary.NextInvoice = formatInvoiceMoney(upcoming.AmountCents, upcoming.Currency)
		summary.NextDetail = "Recorded open invoice dated " + upcoming.CreatedAt.UTC().Format("Jan 2, 2006")
	}
	if len(invoices) > 0 {
		summary.OutstandingDetail = fmt.Sprintf("%d open invoice(s) in recorded history", openCount)
		if openCount == 0 {
			summary.Outstanding = "0 open invoices"
		} else if mixedCurrency {
			summary.Outstanding = "Multiple currencies"
		} else {
			summary.Outstanding = formatInvoiceMoney(outstanding, openCurrency)
		}
	}
	return summary
}

func formatInvoiceMoney(cents int64, currency string) string {
	amount := fmt.Sprintf("%d.%02d", cents/100, cents%100)
	if strings.EqualFold(currency, "USD") {
		return "$" + amount
	}
	if currency == "" {
		return amount + " (currency unreported)"
	}
	return strings.ToUpper(currency) + " " + amount
}

func localDocuments(project core.Project, databaseFilter string) int {
	count := 0
	for _, database := range project.Databases {
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
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
	if value < 0 || *total > math.MaxInt64-value {
		return false
	}
	*total += value
	return true
}

func parseCents(value string) (int64, bool) {
	if !amountPattern.MatchString(value) {
		return 0, false
	}
	wholeText, fraction, hasFraction := strings.Cut(value, ".")
	whole, err := strconv.ParseInt(wholeText, 10, 64)
	if err != nil {
		return 0, false
	}
	cents := int64(0)
	if hasFraction {
		if len(fraction) == 1 {
			fraction += "0"
		}
		cents, err = strconv.ParseInt(fraction, 10, 64)
		if err != nil {
			return 0, false
		}
	}
	if whole > (math.MaxInt64-cents)/100 {
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
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
