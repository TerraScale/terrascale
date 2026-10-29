package v1

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"time"
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
	Disabled       bool       `json:"disabled"`
	UpdatedAt      *time.Time `json:"updatedAt"`
}

// BackupPolicyRequest changes policy. Nil cron removes scheduled snapshots;
// retention is required; nil Disabled uses false.
type BackupPolicyRequest struct {
	FullBackupCron *string `json:"fullBackupCron"`
	RetentionDays  int     `json:"retentionDays"`
	Disabled       *bool   `json:"disabled,omitzero"`
}

// RestoreWindow is the selected destination's proven restore interval.
type RestoreWindow struct {
	Earliest time.Time `json:"earliest"`
	Latest   time.Time `json:"latest"`
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

// CreateBackup creates a consistent snapshot.
func (d *Database) CreateBackup(ctx context.Context) (*DatabaseBackup, error) {
	return databaseCall[DatabaseBackup](ctx, d, http.MethodPost, nil, nil, "backups")
}

// Backups lists ready snapshots using management pagination.
func (d *Database) Backups(ctx context.Context, opts PageOptions) (*Page[DatabaseBackup], error) {
	return databaseCall[Page[DatabaseBackup]](ctx, d, http.MethodGet, opts.query(), nil, "backups")
}

// RestoreBackup creates an independent database from a snapshot.
func (d *Database) RestoreBackup(ctx context.Context, id, name string) (*ManagedDatabase, error) {
	if err := checkID(id); err != nil {
		return nil, err
	}
	if name != "" {
		if err := checkName("database", name); err != nil {
			return nil, err
		}
	}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPost, nil, struct {
		Name string `json:"name,omitempty"`
	}{name}, "backups", id, "restore")
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

// RestorePointInTime restores a new named database at the selected instant.
func (d *Database) RestorePointInTime(ctx context.Context, in PointInTimeRequest) (*ManagedDatabase, error) {
	if in.Name != "" {
		if err := checkName("database", in.Name); err != nil {
			return nil, err
		}
	}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPost, nil, in, "point-in-time", "restore")
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
