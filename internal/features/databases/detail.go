package databases

import (
	"context"
	"errors"
	"net/http"
	"slices"
	"strconv"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/notfound"
)

// detailTabs are the database detail tabs this slice renders; the data tabs
// (documents, SQL, schema, indexes) belong to the data slice.
var detailTabs = map[string]string{"overview": "Overview", "backups": "Backups", "branches": "Branches", "import": "Import and export"}

func mountDetail(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /databases/{databaseID}", func(w http.ResponseWriter, r *http.Request) { renderDetail(w, r, deps, "overview") })
	for _, tab := range []string{"backups", "branches", "import"} {
		mux.HandleFunc("GET /databases/{databaseID}/"+tab, func(w http.ResponseWriter, r *http.Request) { renderDetail(w, r, deps, tab) })
	}
}

type detailData struct {
	Database  core.Database
	Tab       string
	Info      detailInfo
	Error     string
	CanManage bool
	// OpenDelete reopens the delete dialog after a refused delete.
	OpenDelete bool
	// Refill holds the input of a refused backup form.
	Refill backupRefill
	// Choices are the regions the placement form offers; RegionsError
	// explains an empty list.
	Choices      []core.Region
	RegionsError string
}

// backupRefill is what a refused restore or policy form submitted.
type backupRefill struct {
	RestoreBackupID, RestoreName         string
	FullCron, IncrementalCron, Retention string
	Policy                               bool
}

func renderDetail(w http.ResponseWriter, r *http.Request, deps core.Deps, tab string) {
	p, a, project, err := selected(deps, r)
	if err != nil {
		if errors.Is(err, core.ErrUnauthorized) {
			core.RedirectToLogin(w, r)
			return
		}
		writeError(w, err)
		return
	}
	db, ok := project.Databases[r.PathValue("databaseID")]
	if !ok {
		notfound.Render(w, r)
		return
	}
	if !Shows(db, tab) {
		http.Redirect(w, r, TabPath(db.ID, "overview", nil), http.StatusSeeOther)
		return
	}
	org := a.Organizations[p.OrganizationID]
	canManage := org.CanManage(p.UserID) && !core.SystemDatabase(db)
	query := r.URL.Query()
	data := detailData{
		Database:   db,
		Tab:        tab,
		Info:       makeDetailInfo(deps.Demo, p, a, project, db, canManage, canManage && org.CanProvisionResources(p.UserID)),
		Error:      formError(query.Get("error")),
		CanManage:  canManage,
		OpenDelete: tab == "overview" && query.Get("delete") == "1",
	}
	// Only the overview shows the counts and the placement form.
	if tab == "overview" {
		data.Info = data.Info.withContents(r.Context(), deps, p, db)
		if canManage {
			data.Choices, data.RegionsError = regionChoices(r.Context(), deps)
		}
	}
	if data.Error != "" && tab == "backups" {
		refill := func(key string) string { return truncate(query.Get(key), 128) }
		data.Refill = backupRefill{
			RestoreBackupID: refill("restore"), RestoreName: refill("name"),
			FullCron: refill("fullBackupCron"), IncrementalCron: refill("incrementalBackupCron"), Retention: refill("retentionDays"),
			Policy: query.Has("retentionDays"),
		}
	}
	page := core.Page{Layout: "shell", Title: db.Name + " · " + detailTabs[tab], Principal: p, View: func(page core.Page) templ.Component { return detailContent(page, data) }, CSRFToken: deps.CSRF.Token(w, r)}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render database", http.StatusInternalServerError)
	}
}

type detailInfo struct {
	Regions           []detailRegion
	BackupPolicy      core.BackupPolicy
	HasBackupSchedule bool
	BackupsEnabled    bool
	CanToggleBackups  bool
	// Backups are listed newest first.
	Backups             []core.Backup
	CanCreateBackups    bool
	CanEditBackupPolicy bool
	CanRestoreBackups   bool
	CanOverwriteBackups bool
	CanBranch           bool
	Demo                bool
	Collections         int
	Documents           int
	Bytes               int64
	// Unavailable explains a system database TerraBase did not report.
	Unavailable string
}

// withContents adds the database's collection, document and byte counts.
func (info detailInfo) withContents(ctx context.Context, deps core.Deps, p core.Principal, db core.Database) detailInfo {
	ctx, cancel := context.WithTimeout(ctx, countBudget)
	defer cancel()
	var err error
	info.Collections, info.Documents, info.Bytes, err = contents(ctx, deps, p, db)
	if err != nil {
		info.Unavailable = unavailable(err)
	}
	return info
}

// unavailable is what the page says when TerraBase did not report a system
// database's contents.
func unavailable(err error) string { return core.SystemUnavailable(err) }

type detailRegion struct {
	Name     string
	Estimate string
	Sample   bool
}

func makeDetailInfo(demo bool, principal core.Principal, account core.Account, project core.Project, db core.Database, canManage, canMutate bool) detailInfo {
	policy := project.BackupPolicies[db.ID]
	hasSchedule := policy.FullBackupCron != "" || policy.IncrementalBackupCron != ""
	info := detailInfo{
		BackupPolicy:        policy,
		HasBackupSchedule:   hasSchedule,
		BackupsEnabled:      hasSchedule && !policy.Disabled,
		CanToggleBackups:    demo && canMutate && hasSchedule,
		Backups:             slices.SortedFunc(slices.Values(db.Backups), func(a, b core.Backup) int { return b.CreatedAt.Compare(a.CreatedAt) }),
		CanCreateBackups:    demo && canManage,
		CanEditBackupPolicy: demo && canManage,
		CanRestoreBackups:   canMutate,
		CanOverwriteBackups: demo && canManage,
		CanBranch:           canMutate && db.Status == "ready",
		Demo:                demo,
	}

	// The source dashboard's nearest-region 0 ms is illustrative fixture data,
	// not a measured provider latency. Keep it scoped to that exact demo row.
	sample := demo &&
		principal.UserID == "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB" &&
		account.User.ID == principal.UserID && account.User.Email == "mario@terrascale.tech" &&
		principal.OrganizationID == "org_01JC9Q5V2N4P6T8R0X2Z4B6D8" &&
		principal.ProjectID == project.ID &&
		project.ID == "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA" &&
		db.ID == "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC" &&
		db.ProjectID == project.ID &&
		db.Name == "core-primary" && db.Region == "SFO"
	for _, code := range databaseRegions(db) {
		region := detailRegion{Name: regionDisplay(code), Estimate: "Not measured", Sample: sample && code == db.Region}
		if region.Sample {
			region.Estimate = "0 ms · demo estimate"
		}
		info.Regions = append(info.Regions, region)
	}
	if backupRegion := project.BackupRegion; backupRegion != "" && !slices.ContainsFunc(databaseRegions(db), func(code string) bool { return strings.EqualFold(code, backupRegion) }) {
		info.Regions = append(info.Regions, detailRegion{Name: regionDisplay(backupRegion) + " · backup region", Estimate: "Not measured"})
	}
	return info
}

func regionDisplay(code string) string {
	switch strings.ToUpper(code) {
	case "SFO":
		return "SFO · San Francisco"
	case "GRU":
		return "GRU · São Paulo"
	case "FRA":
		return "FRA · Frankfurt"
	case "":
		return "Not configured"
	default:
		return strings.ToUpper(code)
	}
}

func regionListDisplay(codes []string) string {
	if len(codes) == 0 {
		return regionDisplay("")
	}
	labels := make([]string, len(codes))
	for i, code := range codes {
		labels[i] = regionDisplay(code)
	}
	return strings.Join(labels, ", ")
}

// replicationDisplay says whether a placement replicates between regions.
func replicationDisplay(codes []string) string {
	switch len(codes) {
	case 0:
		return "Not reported"
	case 1:
		return "Single region · " + strings.ToUpper(codes[0]) + " only"
	default:
		return "Replicated across " + strings.ToUpper(strings.Join(codes, ", ")) + " · lag not reported"
	}
}

func regionChoiceSummary(codes []string) string {
	if len(codes) == 0 {
		return "Choose regions"
	}
	return regionListDisplay(codes)
}

// namePatternHTML is namePattern for an input's pattern attribute.
const namePatternHTML = `[a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?`

// restoreName refills the restore form of the backup a refused restore used.
// restoreErrorInDialog reports whether a refused restore's error shows in
// the restore dialog of its backup, which renders only while that backup
// exists and the user may restore it; otherwise the page shows the error.
func restoreErrorInDialog(d detailData) bool {
	return d.Error != "" && d.Refill.RestoreBackupID != "" &&
		(d.Info.CanRestoreBackups || d.Info.CanOverwriteBackups) &&
		slices.ContainsFunc(d.Info.Backups, func(b core.Backup) bool { return b.ID == d.Refill.RestoreBackupID })
}

func restoreName(d detailData, backupID string) string {
	if d.Refill.RestoreBackupID == backupID {
		return d.Refill.RestoreName
	}
	return ""
}

// policyValue is a policy field's submitted value after a refused save,
// otherwise the saved one.
func policyValue(d detailData, submitted, saved string) string {
	if d.Refill.Policy {
		return submitted
	}
	return saved
}

func backupRetentionDays(days int) string {
	if days < 1 {
		return "7"
	}
	return strconv.Itoa(days)
}

func backupToggleDisabled(enabled bool) string {
	if enabled {
		return "1"
	}
	return "0"
}

func backupChecked(enabled bool) string {
	if enabled {
		return "true"
	}
	return "false"
}

func backupStatusDescription(info detailInfo) string {
	if !info.HasBackupSchedule {
		return "No scheduled backup policy is configured."
	}
	if info.BackupsEnabled {
		return "The saved backup policy is enabled."
	}
	return "The saved backup policy is disabled."
}

func backupAvailability(info detailInfo) string {
	switch {
	case !info.Demo:
		return "Scheduling is unavailable in the published SDK contract."
	case !info.HasBackupSchedule:
		return "Configure a schedule to enable backups."
	default:
		return "Only project managers can change this policy."
	}
}

// truncate cuts a refilled form value to at most n bytes.
func truncate(value string, n int) string {
	if len(value) > n {
		return value[:n]
	}
	return value
}

func backupTone(status string) string {
	switch status {
	case "completed", "ready", "succeeded":
		return "success"
	case "failed":
		return "danger"
	default:
		return "neutral"
	}
}

func branchCapability(demo bool) string {
	if demo {
		return "In-memory snapshot copy"
	}
	return "SDK branch request"
}
