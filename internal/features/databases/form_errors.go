package databases

import (
	"net/http"
	"net/url"

	"terrascale.tech/dashboard/internal/core"
)

// Error codes a failed database or backup form sends back to its page. The
// pages show only the messages below, so a crafted link cannot put arbitrary
// text on them.
const (
	ErrInvalidName    = "invalid-name"
	ErrInvalidRegions = "invalid-regions"
	ErrNameTaken      = "name-taken"
	ErrPlanLimit      = "plan-limit"
	ErrConfirmName    = "confirm-name"
	ErrProvisioning   = "provisioning"
	ErrNotReady       = "not-ready"
	ErrForbidden      = "forbidden"
	ErrMissing        = "missing"
	ErrUnavailable    = "unavailable"
	ErrProvider       = "provider"
	ErrRetention      = "retention"
	ErrSchedule       = "schedule"
	ErrBackupMissing  = "backup-missing"
	ErrBackupsOffline = "backups-offline"
)

var formErrors = map[string]string{
	ErrInvalidName:    "Use 1 to 63 lowercase letters, digits, and hyphens, starting and ending with a letter or digit.",
	ErrInvalidRegions: "Select one or more valid regions for this database.",
	ErrNameTaken:      "Another database in this project already uses that name. Choose a different one.",
	ErrPlanLimit:      "Your plan's database limit is reached. Upgrade the plan or delete a database to create another.",
	ErrConfirmName:    "The name you typed does not match. Type the database name exactly to delete it.",
	ErrProvisioning:   "This database is still provisioning. Try again once it is ready.",
	ErrNotReady:       "Only a ready database can be branched.",
	ErrForbidden:      "Your role cannot make this change. Ask an organization owner or admin.",
	ErrMissing:        "That database or backup no longer exists.",
	ErrUnavailable:    "The data plane is unavailable. Try again shortly.",
	ErrProvider:       "The data plane refused the request. Try again, or contact support if it keeps failing.",
	ErrRetention:      "Set snapshot retention from 1 to 35 days.",
	ErrSchedule:       "Enter schedules as five cron fields, such as 0 2 * * *, or leave them empty.",
	ErrBackupMissing:  "That restore point is no longer available.",
	ErrBackupsOffline: "On-demand backups and schedules are unavailable in the published SDK contract.",
}

// formError is the message for an error code, or "" for an unknown one.
func formError(code string) string { return formErrors[code] }

// FormFailed answers a form post that was refused. A browser returns to path
// with the error code, and extra values that refill the form; an API client
// gets status and the message as plain text.
func FormFailed(w http.ResponseWriter, r *http.Request, status int, path, code string, extra url.Values) {
	if !core.WantsHTML(r) {
		http.Error(w, formErrors[code], status)
		return
	}
	query := url.Values{"error": {code}}
	for key, values := range extra {
		query[key] = values
	}
	http.Redirect(w, r, path+"?"+query.Encode(), http.StatusSeeOther)
}
