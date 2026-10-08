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
	"terrascale.tech/dashboard/internal/features/replication"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/ui/charts"
)

var amountPattern = regexp.MustCompile(`^\d+(?:\.\d{1,2})?$`)

type databaseRow struct {
	ID           string
	Name         string
	Region       string
	SpendCap     string
	Effective    string
	Status       string
	Documents    string
	LogicalSize  string
	SharePercent int
	Metered      bool
	Reads        int64
	Writes       int64
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
	LogicalSize   string
	SharePercent  int
}

// allowanceGauge is one plan allowance and this month's use of it.
type allowanceGauge struct {
	Name      string
	Allowance string
	Used      string
	Note      string
	// HasRing is set when both the use and the limit are known.
	HasRing bool
	Percent int
	Ring    charts.RingData
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

type pageData struct {
	InternalBill          *internalBill
	Replication           replication.View
	OrganizationBilling   bool
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
	Allowances            []allowanceGauge
	AllowanceError        string
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
	InvoicePage           invoicePage
	Reads                 int64
	Writes                int64
	LogicalSize           string
	// StorageMeasured covers the selected scope's storage and documents;
	// OrganizationStorageMeasured the projects' storage.
	StorageMeasured             bool
	OrganizationStorageMeasured bool
	UsageSource                 string
	UsageError                  string
	UsageAvailable              bool
	Saved                       string
	CanManage                   bool
	CanManageBilling            bool
	InvoiceCount                int
	ActiveDatabases             int
	RegionCount                 int
}

type billingPlanChoice struct {
	Key, Name, MonthlyPrice, AnnualPrice, Storage, Reads, Writes, Databases, Members string
}

type billingPlanReview struct {
	Open, Ready, Immediate                                                                          bool
	Key, Name, Interval, Price, Effect, Estimate, Storage, Reads, Writes, Databases, Members, Error string
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

// Mount registers the usage page, spending controls, and their tools.
// billing is nil in demo mode.
func Mount(mux *http.ServeMux, deps core.Deps, billing *payments.Stripe) {
	service := Service{deps: deps}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /usage", pageHandler(deps, billing, nil))

	mux.HandleFunc("POST /api/v1/dashboard/usage/spending-controls", spendingHandler(deps, service))
}

// OrganizationBilling renders existing billing controls through the organization shell.
func OrganizationBilling(deps core.Deps, billing *payments.Stripe, wrap func(core.Page, templ.Component) templ.Component) http.HandlerFunc {
	return pageHandler(deps, billing, wrap)
}

func pageHandler(deps core.Deps, billing *payments.Stripe, wrap func(core.Page, templ.Component) templ.Component) http.HandlerFunc {
	service := Service{deps: deps}
	return func(w http.ResponseWriter, r *http.Request) {
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
		if wrap == nil && tab == "billing" {
			http.Redirect(w, r, "/organization?"+r.URL.RawQuery, http.StatusSeeOther)
			return
		}
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
		ctx := r.Context()
		token := ""
		if tab == "overview" || org.Billing.Internal {
			token = core.UserToken(r.Context())
		}
		data, err := buildPage(ctx, service, p, token, org, project, tab, databaseFilter, r.URL.Query().Get("range"))
		if err != nil {
			writeLoadError(w, err)
			return
		}
		data.ExplicitProjectFilter = r.URL.Query().Get("project") == project.ID
		if tab == "overview" {
			data.Replication = replication.Load(ctx, deps, p, project.ID, databaseFilter, data.HistoryRange)
		}
		if demovisuals.IsSeededMarioAccount(deps.Demo, account, org) {
			addMarioSampleVisuals(&data)
		}
		now := time.Now().UTC()
		data.InvoicePage = makeInvoicePage(
			org.Billing.Invoices,
			r.URL.Query().Get("status"),
			r.URL.Query().Get("issued"),
			r.URL.Query().Get("page"),
			now,
		)
		data.CanManageBilling = org.OwnerUserID == p.UserID && !org.Billing.Internal
		data.Personal = org.Kind == "personal"
		data.Demo = deps.Demo
		data.BillingPlanKey = org.Billing.PlanKey
		data.BillingInterval = org.Billing.Interval
		data.BillingSubscription = org.Billing.SubscriptionID != "" && org.Billing.Status != "canceled" && org.Billing.Status != "incomplete_expired"
		if data.ScheduledPlan, err = planName(ctx, deps.Store, org.Billing.ScheduledPlanKey); err != nil {
			writeLoadError(w, err)
			return
		}
		if data.PendingPlan, err = planName(ctx, deps.Store, org.Billing.PendingPlanKey); err != nil {
			writeLoadError(w, err)
			return
		}
		data.ScheduledAt = org.Billing.ScheduledAt
		if data.BillingPlanKey == "" && org.PlanID == core.FreePlanID {
			data.BillingPlanKey = "free"
		}
		for _, key := range []string{"starter", "pro", "business"} {
			plan, err := deps.Store.Plan(ctx, key)
			if err != nil && !errors.Is(err, core.ErrNotFound) {
				writeLoadError(w, err)
				return
			}
			if err != nil || !plan.Visible || plan.PriceCents <= 0 {
				continue
			}
			data.BillingPlans = append(data.BillingPlans, billingPlanChoice{
				Key: key, Name: plan.Name, MonthlyPrice: priceUSD(plan.PriceCents), AnnualPrice: priceUSD(plan.AnnualPriceCents),
				Storage: plan.StorageAllowance(), Reads: plan.ReadsAllowance(), Writes: plan.WritesAllowance(), Databases: plan.DatabaseAllowance(), Members: plan.MemberAllowance(),
			})
		}
		plan, err := deps.Store.Plan(ctx, org.PlanID)
		if err != nil && !errors.Is(err, core.ErrNotFound) {
			writeLoadError(w, err)
			return
		}
		if err == nil {
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
		data.OrganizationBilling = wrap != nil
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
		var plans templ.Component
		csrf := ""
		if wrap != nil {
			if deps.CSRF != nil {
				csrf = deps.CSRF.Token(w, r)
			}
			plans, err = subscriptions.BillingContent(deps, r, p, csrf)
			if err != nil {
				writeLoadError(w, err)
				return
			}
		}
		page := core.Page{
			Layout: "shell", Title: "Usage", Principal: p, CSRFToken: csrf,
			View: func(view core.Page) templ.Component {
				if wrap != nil {
					return wrap(view, organizationBillingContent(data, view.CSRFToken, plans))
				}
				return usageView(data, view.CSRFToken)
			},
		}
		if wrap != nil {
			page.Title = "Organization · TerraScale"
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render usage", http.StatusInternalServerError)
		}
	}
}

func spendingHandler(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, _, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		if err := core.Can(r.Context(), deps.Store, p, core.Organization.CanManage); err != nil {
			writeError(w, err)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		var controls spendingControls
		organizationCap, hasOrganizationCap, valid := formCap(r.Form, "spendCap")
		if !valid {
			redirect(w, r, "invalid")
			return
		}
		if hasOrganizationCap {
			paidExcess := r.FormValue("paidExcess") == "true" || r.FormValue("paidExcess") == "on"
			controls.OrganizationCapCents, controls.PaidExcess = &organizationCap, &paidExcess
		}
		if controls.ProjectCaps, valid = capsFromForm(r.Form, "projCap_"); !valid {
			redirect(w, r, "invalid")
			return
		}
		if controls.DatabaseCaps, valid = capsFromForm(r.Form, "dbCap_"); !valid {
			redirect(w, r, "invalid")
			return
		}
		if err := service.SetSpendingControls(r.Context(), p, controls); err != nil {
			writeError(w, err)
			return
		}
		redirect(w, r, "ok")
	}
}

func priceUSD(cents int64) string {
	if cents < 0 {
		return "-" + priceUSD(-cents)
	}
	return fmt.Sprintf("US$ %d.%02d", cents/100, cents%100)
}

// planLoadError is shown when a plan review cannot read the plans.
const planLoadError = "Could not load plans. Please try again."

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
		free, err := deps.Store.Plan(r.Context(), "free")
		switch {
		case err == nil:
			review.Storage, review.Reads, review.Writes = free.StorageAllowance(), free.ReadsAllowance(), free.WritesAllowance()
			review.Databases, review.Members = free.DatabaseAllowance(), free.MemberAllowance()
			review.Warnings = limitWarnings(org, free, time.Now())
		case !errors.Is(err, core.ErrNotFound):
			// Without the Free plan's limits the review cannot warn about
			// what the change would block.
			review.Error = planLoadError
			return review
		}
		review.Ready = true
		return review
	}
	if interval != "monthly" && interval != "annual" {
		review.Error = "Choose monthly or annual billing."
		return review
	}
	plan, err := deps.Store.Plan(r.Context(), key)
	if err != nil && !errors.Is(err, core.ErrNotFound) {
		review.Error = planLoadError
		return review
	}
	if err != nil || !plan.Visible || plan.PriceCents <= 0 || interval == "annual" && plan.AnnualPriceCents <= 0 {
		review.Error = "Choose an available plan."
		return review
	}
	review.Name = plan.Name
	review.Price = priceUSD(plan.PriceCents) + " / month"
	if interval == "annual" {
		review.Price = priceUSD(plan.AnnualPriceCents) + " / year"
	}
	review.Storage, review.Reads, review.Writes = plan.StorageAllowance(), plan.ReadsAllowance(), plan.WritesAllowance()
	review.Databases, review.Members = plan.DatabaseAllowance(), plan.MemberAllowance()
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
// Store failures other than a missing plan are returned.
func planName(ctx context.Context, store core.Store, key string) (string, error) {
	if key == "" {
		return "", nil
	}
	plan, err := store.Plan(ctx, key)
	if errors.Is(err, core.ErrNotFound) {
		return key, nil
	}
	if err != nil {
		return "", err
	}
	return plan.Name, nil
}

// writeLoadError answers a usage page whose data could not be loaded: 503
// while the store is unavailable, 500 otherwise.
func writeLoadError(w http.ResponseWriter, err error) {
	if errors.Is(err, core.ErrUnavailable) {
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
		return
	}
	http.Error(w, "could not load usage", http.StatusInternalServerError)
}

func buildPage(ctx context.Context, service Service, p core.Principal, token string, org core.Organization, project core.Project, tab, databaseFilter, rangeKey string) (pageData, error) {
	report, err := service.report(ctx, p, token, org, project, databaseFilter, rangeKey)
	if err != nil {
		return pageData{}, err
	}
	projects := make([]projectRow, 0, len(report.Projects))
	for _, item := range report.Projects {
		projects = append(projects, projectRow{
			ID: item.ID, Name: item.Name, Region: item.Region, DatabaseCount: item.DatabaseCount,
			SpendCap: dollars(item.SpendCapCents), Effective: capLabel(item.EffectiveCapCents), Selected: item.Selected,
			LogicalSize: measured(item.Unavailable, formatBytes(item.LogicalBytes)), SharePercent: item.SharePercent,
		})
	}
	databases := make([]databaseRow, 0, len(report.Databases))
	for _, item := range report.Databases {
		databases = append(databases, databaseRow{
			ID: item.ID, Name: item.Name, Region: item.Region, SpendCap: dollars(item.SpendCapCents), Effective: capLabel(item.EffectiveCapCents),
			Status: item.Status, Documents: measured(item.Unavailable, strconv.FormatInt(item.Documents, 10)),
			LogicalSize: measured(item.Unavailable, formatBytes(item.LogicalBytes)), SharePercent: item.SharePercent,
			Metered: item.Metered, Reads: item.Reads, Writes: item.Writes,
		})
	}
	databaseOptions := make([]databaseOption, 0, len(project.Databases))
	for _, database := range project.Databases {
		databaseOptions = append(databaseOptions, databaseOption{ID: database.ID, Name: database.Name})
	}
	slices.SortFunc(databaseOptions, func(a, b databaseOption) int { return strings.Compare(a.Name, b.Name) })
	data := pageData{
		Tab: tab, Projects: projects, DatabaseOptions: databaseOptions, Databases: databases, SelectedProject: project.ID,
		SelectedDatabase: databaseFilter, OrganizationCap: dollars(report.SpendCapCents), InternalBill: report.InternalBill,
		PaidExcess: report.PaidExcessEnabled, EffectiveCap: capLabel(report.ProjectEffectiveCapCents),
		PlanName: report.PlanName, PlanUsageNote: report.PlanUsageNote, AllowanceError: report.AllowanceError, BillingStatus: org.BillingStatus, BillingRenewal: org.BillingRenewal,
		BillingSummary:  summarizeBilling(org.Billing.Invoices, time.Now().UTC()),
		InvoiceCount:    len(org.Billing.Invoices),
		ActiveDatabases: report.ActiveDatabases, RegionCount: report.RegionCount, CanManage: report.CanManage,
		Reads: report.Totals.Reads, Writes: report.Totals.Writes, LogicalSize: formatBytes(report.Totals.LogicalBytes),
		UsageSource: report.Totals.Source, UsageError: report.Totals.Error, UsageAvailable: report.Totals.Available,
		StorageMeasured: report.Totals.StorageMeasured, OrganizationStorageMeasured: report.Allowances[2].Measured,
		HistoryRange: report.Totals.Range, HistoryRangeLabel: demovisuals.RangeLabel(report.Totals.Range),
	}
	for _, item := range report.Allowances {
		gauge := allowanceGauge{Name: item.Name, Allowance: item.Allowance, Used: "Not metered", Note: item.Allowance}
		if item.Measured {
			gauge.Used = formatCount(item.Used)
			if item.Name == "Storage" {
				gauge.Used = formatBytes(item.Used)
			}
		}
		if item.Measured && item.Limit > 0 {
			gauge.HasRing, gauge.Percent = true, item.Percent
			gauge.Ring = charts.NewRing(item.Name, item.Used, item.Limit)
			gauge.Ring.Label = fmt.Sprintf("%s: %d%% of the %s plan's allowance", item.Name, item.Percent, report.PlanName)
		}
		data.Allowances = append(data.Allowances, gauge)
	}
	if report.Totals.Available {
		reads, writes, labels := make([]int64, len(report.Totals.Series)), make([]int64, len(report.Totals.Series)), make([]string, len(report.Totals.Series))
		for i, point := range report.Totals.Series {
			reads[i], writes[i], labels[i] = point.Reads, point.Writes, bucketLabel(point.Start, data.HistoryRange)
		}
		// The axis takes each tick's bucket timestamp.
		data.HistoryChart = charts.NewLine("Reads and writes, "+data.HistoryRangeLabel, nil, labels, 200,
			charts.Series{Name: "Reads", Values: reads}, charts.Series{Name: "Writes", Values: writes})
		data.HistoryTotals = []historyTotal{
			{Name: "Reads", Value: report.Totals.Reads, Class: "ts-cat-1"},
			{Name: "Writes", Value: report.Totals.Writes, Class: "ts-cat-2"},
		}
	}
	return data, nil
}

// measured is value, or "Unavailable" when TerraBase did not report it.
func measured(unavailable bool, value string) string {
	if unavailable {
		return "Unavailable"
	}
	return value
}

// bucketLabel names a series bucket by its UTC start: the hour for hourly
// ranges, the day for 30d.
func bucketLabel(start time.Time, rangeKey string) string {
	if rangeKey == "30d" {
		return start.UTC().Format("Jan 2")
	}
	return start.UTC().Format("Jan 2, 15:04 UTC")
}

// formatCount writes n with thousands separators, such as 8,140,200.
func formatCount(n int64) string {
	text := strconv.FormatInt(n, 10)
	for i := len(text) - 3; i > 0 && text[i-1] != '-'; i -= 3 {
		text = text[:i] + "," + text[i:]
	}
	return text
}

// makeInvoicePage pages invoices for the billing tab. Filter values the
// page doesn't offer are ignored rather than rejected.
func makeInvoicePage(invoices []core.Invoice, rawStatus, rawIssued, rawPage string, now time.Time) invoicePage {
	statusOptions := []invoiceFilterOption{
		{Value: "paid", Label: "Paid"},
		{Value: "open", Label: "Open"},
		{Value: "void", Label: "Void"},
	}
	issuedOptions := invoiceIssuedOptions(now)
	offered := func(options []invoiceFilterOption, value string) string {
		if slices.ContainsFunc(options, func(option invoiceFilterOption) bool { return option.Value == value }) {
			return value
		}
		return ""
	}
	status, issued := offered(statusOptions, rawStatus), offered(issuedOptions, rawIssued)
	number, err := strconv.Atoi(rawPage)
	if err != nil {
		number = 1
	}
	found, _ := subscriptions.PageInvoices(invoices, status, issued, number, now)
	rows := make([]invoiceRow, 0, len(found.Invoices))
	for _, invoice := range found.Invoices {
		rows = append(rows, invoiceRow{
			Number: invoice.Number, Status: invoice.Status,
			Amount:    fmt.Sprintf("%s %d.%02d", strings.ToUpper(invoice.Currency), invoice.AmountCents/100, invoice.AmountCents%100),
			CreatedAt: invoice.CreatedAt, PeriodStart: invoice.PeriodStart, PeriodEnd: invoice.PeriodEnd,
			HostedURL: subscriptions.InvoiceURL(invoice.HostedURL), PDFURL: subscriptions.InvoiceURL(invoice.PDFURL),
		})
	}
	first := 0
	if found.Total > 0 {
		first = (found.Page-1)*subscriptions.InvoicePageSize + 1
	}
	return invoicePage{
		Rows: rows, Status: status, Issued: issued,
		StatusOptions: statusOptions, IssuedOptions: issuedOptions,
		Page: found.Page, PageCount: found.PageCount, Total: found.Total, First: first,
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
	return "/organization?" + query.Encode()
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
	account, err := deps.Store.SnapshotAs(r.Context(), p)
	if err != nil {
		writeError(w, err)
		return core.Principal{}, core.Account{}, false
	}
	return p, account, true
}

func formatBytes(value int64) string {
	switch {
	case value < 1<<10:
		return fmt.Sprintf("%d B", value)
	case value < 1<<20:
		return fmt.Sprintf("%.1f KiB", float64(value)/(1<<10))
	case value < 1<<30:
		return fmt.Sprintf("%.1f MiB", float64(value)/(1<<20))
	}
	return fmt.Sprintf("%.1f GiB", float64(value)/(1<<30))
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

// capLabel describes an effective spend cap in cents; 0 means no limit.
func capLabel(cents int64) string {
	if cents <= 0 {
		return "No limit"
	}
	return "$" + dollars(cents)
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
