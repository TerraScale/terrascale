package v1

import (
	"context"
	"errors"
	"net/http"
	"time"
)

// ExportSchedule describes scheduled NDJSON exports to the project's customer
// backup destination. Nil fields describe a schedule that has not been saved.
// These exports use Database.Export's document format, separate from snapshots.
type ExportSchedule struct {
	Cron      *string    `json:"cron"`
	UpdatedAt *time.Time `json:"updatedAt"`
	LastRun   *ExportRun `json:"lastRun"`
}

// ExportRun is the last scheduled attempt. Status is "succeeded" or "failed".
// A success has an object Key and no Error. A failure has no Key, zero Bytes
// and a sanitized Error; bucket responses and credentials are never returned.
// Documents may count documents read before a failed upload. Failed attempts
// wait for the next cron occurrence; the SDK does not schedule or replay runs.
type ExportRun struct {
	StartedAt  time.Time `json:"startedAt"`
	FinishedAt time.Time `json:"finishedAt"`
	Status     string    `json:"status"`
	Key        *string   `json:"key"`
	Documents  int64     `json:"documents"`
	Bytes      int64     `json:"bytes"`
	Error      *string   `json:"error"`
}

// ExportScheduleRequest replaces the cron. A nil, empty or whitespace Cron
// stops scheduled exports, retaining LastRun. Otherwise Cron is a standard
// five-field UTC expression; the server trims and validates it.
type ExportScheduleRequest struct {
	Cron *string `json:"cron"`
}

// ExportSchedule reads the schedule using databases:read. The project token
// must match the project and any database restriction; mismatches return 404.
func (d *Database) ExportSchedule(ctx context.Context) (*ExportSchedule, error) {
	return d.exportSchedule(ctx, http.MethodGet, nil)
}

// SetExportSchedule saves a schedule using databases:write and organization
// writer access. A nonempty cron requires a configured project backup
// destination (409 conflict otherwise); invalid cron returns 400 invalid_request.
// Each run writes terrabase-exports/{project}/{database}/{yyyyMMddTHHmmssZ}.ndjson
// to that customer bucket. This mutation is never automatically replayed.
func (d *Database) SetExportSchedule(ctx context.Context, in ExportScheduleRequest) (*ExportSchedule, error) {
	return d.exportSchedule(ctx, http.MethodPut, in)
}

func (d *Database) exportSchedule(ctx context.Context, method string, in any) (*ExportSchedule, error) {
	out, err := databaseCall[ExportSchedule](ctx, d, method, nil, in, "export-schedule")
	if err != nil {
		return nil, err
	}
	if run := out.LastRun; run != nil {
		valid := !run.StartedAt.IsZero() && !run.FinishedAt.IsZero() && run.Documents >= 0 && run.Bytes >= 0
		switch run.Status {
		case "succeeded":
			valid = valid && run.Key != nil && *run.Key != "" && run.Error == nil
		case "failed":
			valid = valid && run.Key == nil && run.Bytes == 0 && run.Error != nil && *run.Error != ""
		default:
			valid = false
		}
		if !valid {
			return nil, errors.New("terrabase: invalid export schedule run response")
		}
	}
	return out, nil
}
