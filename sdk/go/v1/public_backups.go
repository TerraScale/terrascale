package v1

import (
	"cmp"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"time"

	"terrascale.tech/sdk/internal/httpx"
)

// DatabaseBackup is a completed manual or scheduled snapshot.
type DatabaseBackup struct {
	ID         string    `json:"id" ulid:"required"`
	DatabaseID string    `json:"databaseId" ulid:"required"`
	Status     string    `json:"status"`
	Trigger    string    `json:"trigger"`
	SizeBytes  int64     `json:"sizeBytes"`
	CreatedAt  time.Time `json:"createdAt"`
}

// BackupPolicy describes full snapshot scheduling and retention.
type BackupPolicy struct {
	FullBackupCron *string    `json:"fullBackupCron"`
	RetentionDays  int        `json:"retentionDays"`
	UpdatedAt      *time.Time `json:"updatedAt"`
}

// BackupPolicyRequest changes policy. Nil cron removes scheduled snapshots;
// retention is required.
type BackupPolicyRequest struct {
	FullBackupCron *string `json:"fullBackupCron"`
	RetentionDays  int     `json:"retentionDays"`
}

// RestoreWindow is the selected destination's proven restore interval.
type RestoreWindow struct {
	Earliest time.Time `json:"earliest"`
	Latest   time.Time `json:"latest"`
}

func (r *RestoreWindow) UnmarshalJSON(data []byte) error {
	type window RestoreWindow
	var wire struct {
		window
		EarliestRestorePointUTC time.Time `json:"earliestRestorePointUtc"`
		LatestRestorePointUTC   time.Time `json:"latestRestorePointUtc"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	*r = RestoreWindow{
		Earliest: cmp.Or(wire.EarliestRestorePointUTC, wire.Earliest),
		Latest:   cmp.Or(wire.LatestRestorePointUTC, wire.Latest),
	}
	return nil
}

// PointInTimePlan previews backup metadata; it does not prove object integrity.
type PointInTimePlan struct {
	Window          RestoreWindow `json:"window"`
	PointInTime     time.Time     `json:"pointInTime"`
	SnapshotAt      time.Time     `json:"snapshotAt"`
	LatestSegmentAt *time.Time    `json:"latestSegmentAt"`
	SegmentCount    int           `json:"segmentCount"`
	HistoryCheck    string        `json:"historyCheck"`
}

// PointInTimeRequest selects a restore instant and platform (default) or
// customer destination. Empty Name lets the producer choose a restore name.
type PointInTimeRequest struct {
	PointInTime time.Time `json:"pointInTime"`
	Destination string    `json:"destination,omitempty"`
	Name        string    `json:"name,omitempty"`
}

type restoreTarget struct {
	PointInTime time.Time `json:"pointInTime"`
}

type restoreRequest struct {
	Name        string        `json:"name,omitempty"`
	Target      restoreTarget `json:"target"`
	TTLSeconds  *int64        `json:"ttlSeconds,omitempty"`
	Destination string        `json:"destination,omitempty"`
}

// CreateBackup creates a consistent snapshot.
func (d *Database) CreateBackup(ctx context.Context) (*DatabaseBackup, error) {
	return databaseCall[DatabaseBackup](ctx, d, http.MethodPost, nil, nil, "backups")
}

// Backups lists ready snapshots using management pagination.
func (d *Database) Backups(ctx context.Context, opts PageOptions) (*Page[DatabaseBackup], error) {
	return databaseCall[Page[DatabaseBackup]](ctx, d, http.MethodGet, opts.query(), nil, "backups")
}

// RestoreOperation is a background restore started by a restore route.
type RestoreOperation struct {
	ID           string     `json:"id" ulid:"required"`
	OperationID  string     `json:"operationId" ulid:"required"`
	Status       string     `json:"status"`
	Kind         string     `json:"kind"`
	Database     string     `json:"database"`
	ErrorCode    *string    `json:"errorCode"`
	ErrorMessage *string    `json:"errorMessage"`
	CreatedAt    time.Time  `json:"createdAt"`
	FinishedAt   *time.Time `json:"finishedAt"`
}

func (o *RestoreOperation) UnmarshalJSON(data []byte) error {
	type operation RestoreOperation
	var wire struct {
		operation
		State            string     `json:"state"`
		TargetDatabaseID string     `json:"targetDatabaseId"`
		StartedAtUTC     time.Time  `json:"startedAtUtc"`
		UpdatedAtUTC     *time.Time `json:"updatedAtUtc"`
		FailureMessage   *string    `json:"failureMessage"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	switch wire.State {
	case "":
	case "pending", "running", "succeeded", "failed":
		wire.ID = wire.OperationID
		wire.Database = wire.TargetDatabaseID
		wire.CreatedAt = wire.StartedAtUTC
		wire.ErrorMessage = wire.FailureMessage
		wire.Status = wire.State
		if wire.State == "succeeded" {
			wire.Status = "completed"
		}
		if wire.Status == "completed" || wire.Status == "failed" {
			wire.FinishedAt = wire.UpdatedAtUTC
		}
		if err := checkID(wire.Database); err != nil {
			return fmt.Errorf("terrabase: restore target: %w", err)
		}
	default:
		return fmt.Errorf("terrabase: invalid restore operation state %q", wire.State)
	}
	*o = RestoreOperation(wire.operation)
	return nil
}

const restorePollInterval = 250 * time.Millisecond
const restoreDeadline = 10 * time.Minute

// GetRestoreOperation reads one background restore operation of this database.
func (d *Database) GetRestoreOperation(ctx context.Context, operationID string) (*RestoreOperation, error) {
	if err := checkID(operationID); err != nil {
		return nil, err
	}
	return databaseCall[RestoreOperation](ctx, d, http.MethodGet, nil, nil, "restores", operationID)
}

// awaitRestore polls a background restore to its ten-minute deadline and reads
// the restored database. A failed operation and the deadline are APIError.
func (d *Database) awaitRestore(ctx context.Context, operation *RestoreOperation) (*ManagedDatabase, error) {
	deadline := time.Now().Add(restoreDeadline)
	var err error
	for operation.Status == "pending" || operation.Status == "running" {
		if !time.Now().Before(deadline) {
			return nil, &APIError{Status: http.StatusOK, Code: "restore_timeout", Message: "the restore operation did not finish within ten minutes"}
		}
		if err := httpx.Wait(ctx, restorePollInterval); err != nil {
			return nil, err
		}
		operation, err = d.GetRestoreOperation(ctx, operation.ID)
		if err != nil {
			return nil, err
		}
	}
	if operation.Status != "completed" {
		code, message := "restore_failed", "the restore operation failed"
		if operation.ErrorCode != nil {
			code = cmp.Or(*operation.ErrorCode, code)
		}
		if operation.ErrorMessage != nil {
			message = *operation.ErrorMessage
		}
		return nil, &APIError{Status: http.StatusOK, Code: ErrorCode(code), Message: message}
	}
	return d.c.Database(d.project, operation.Database).Info(ctx)
}

// RestoreBackup creates an independent database from a snapshot, polling the
// background operation to its ten-minute deadline before returning the database.
func (d *Database) RestoreBackup(ctx context.Context, id, name string) (*ManagedDatabase, error) {
	if err := checkID(id); err != nil {
		return nil, err
	}
	if name != "" {
		if err := checkName("database", name); err != nil {
			return nil, err
		}
	}
	return d.startRestore(ctx, struct {
		Name string `json:"name,omitempty"`
	}{name}, "backups", id, "restore")
}

func (d *Database) startRestore(ctx context.Context, body any, path ...string) (*ManagedDatabase, error) {
	operation, err := databaseCall[RestoreOperation](ctx, d, http.MethodPost, nil, body, path...)
	if err != nil {
		return nil, err
	}
	return d.awaitRestore(ctx, operation)
}

// BackupPolicy reads the snapshot policy.
func (d *Database) BackupPolicy(ctx context.Context) (*BackupPolicy, error) {
	return databaseCall[BackupPolicy](ctx, d, http.MethodGet, nil, nil, "backup-policy")
}

// SetBackupPolicy replaces the snapshot policy.
func (d *Database) SetBackupPolicy(ctx context.Context, in BackupPolicyRequest) (*BackupPolicy, error) {
	return databaseCall[BackupPolicy](ctx, d, http.MethodPut, nil, in, "backup-policy")
}

// RestoreWindow reads the platform or customer restore interval. Empty uses platform.
func (d *Database) RestoreWindow(ctx context.Context, destination string) (*RestoreWindow, error) {
	q := url.Values{}
	if destination != "" {
		q.Set("destination", destination)
	}
	return databaseCall[RestoreWindow](ctx, d, http.MethodGet, q, nil, "point-in-time")
}

// PlanPointInTime previews a restore without creating a database.
func (d *Database) PlanPointInTime(ctx context.Context, in PointInTimeRequest) (*PointInTimePlan, error) {
	// The preview request has no name field in the producer contract.
	return databaseCall[PointInTimePlan](ctx, d, http.MethodPost, nil, struct {
		PointInTime time.Time `json:"pointInTime"`
		Destination string    `json:"destination,omitempty"`
	}{in.PointInTime, in.Destination}, "point-in-time", "plan")
}

// RestorePointInTime restores a new named database at the selected instant,
// polling the background operation to its ten-minute deadline before returning.
func (d *Database) RestorePointInTime(ctx context.Context, in PointInTimeRequest) (*ManagedDatabase, error) {
	if in.Name != "" {
		if err := checkName("database", in.Name); err != nil {
			return nil, err
		}
	}
	return d.startRestore(ctx, restoreRequest{
		Name: in.Name, Target: restoreTarget{PointInTime: in.PointInTime}, Destination: in.Destination,
	}, "point-in-time", "restore")
}

// RewindPointInTime replaces the database's tables in place with their state at the selected instant.
func (d *Database) RewindPointInTime(ctx context.Context, in PointInTimeRequest) (*ManagedDatabase, error) {
	return d.startRestore(ctx, struct {
		PointInTime time.Time `json:"pointInTime"`
		Destination string    `json:"destination,omitempty"`
	}{in.PointInTime, in.Destination}, "rewind")
}

// ProjectBackupDestination is customer bucket metadata without credentials.
type ProjectBackupDestination struct {
	Endpoint  string    `json:"endpoint"`
	Region    string    `json:"region"`
	Bucket    string    `json:"bucket"`
	Mode      string    `json:"mode"`
	UpdatedAt time.Time `json:"updatedAt"`
}

// ProjectBackupDestinationRequest replaces a destination. Credentials are
// write-only; formatting this request never prints them.
type ProjectBackupDestinationRequest struct {
	Endpoint    string `json:"endpoint"`
	Region      string `json:"region"`
	Bucket      string `json:"bucket"`
	Mode        string `json:"mode"`
	AccessKeyID string `json:"accessKeyId"`
	SecretKey   string `json:"secretKey"`
}

func (ProjectBackupDestinationRequest) String() string {
	return "terrabase backup destination (credentials redacted)"
}
func (in ProjectBackupDestinationRequest) Format(f fmt.State, _ rune) { fmt.Fprint(f, in.String()) }

// BackupDestination reads the project-wide customer bucket configuration.
func (c *Client) BackupDestination(ctx context.Context, project string) (*ProjectBackupDestination, error) {
	return projectCall[ProjectBackupDestination](ctx, c, http.MethodGet, project, nil, nil, "backup-destination")
}

// SetBackupDestination replaces the customer bucket and its write-only credentials.
func (c *Client) SetBackupDestination(ctx context.Context, project string, in ProjectBackupDestinationRequest) (*ProjectBackupDestination, error) {
	return projectCall[ProjectBackupDestination](ctx, c, http.MethodPut, project, nil, in, "backup-destination")
}

// DeleteBackupDestination resumes platform shipping, preserving customer objects.
func (c *Client) DeleteBackupDestination(ctx context.Context, project string) error {
	req, err := projectRequest(http.MethodDelete, project, nil, "backup-destination")
	if err != nil {
		return err
	}
	_, err = c.call(ctx, req, nil, nil)
	return err
}
