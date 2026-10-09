package backups

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// ErrUnavailable marks a backup action the live provider contract cannot
// perform.
var ErrUnavailable = errors.New("on-demand backups and backup scheduling are unavailable in the published SDK contract")

const maxRetentionDays = 35

var (
	errRetention     = core.Invalid("Set snapshot retention from 1 to 35 days.")
	errSchedule      = core.Invalid("Enter schedules as five cron fields, such as 0 2 * * *, or leave them empty.")
	errRestoreName   = core.Invalid("Name the restored database with 1 to 63 lowercase letters, digits, and inner hyphens.")
	errConfirmName   = core.Invalid("Type the database name to confirm overwriting its data.")
	errBackupMissing = fmt.Errorf("%w: that restore point is no longer available", core.ErrNotFound)
)

type backupView struct {
	ID        string    `json:"id"`
	Status    string    `json:"status"`
	CreatedAt time.Time `json:"createdAt"`
	SizeBytes int64     `json:"sizeBytes"`
}

type policyView struct {
	FullBackupCron        string    `json:"fullBackupCron" jsonschema:"Cron schedule for full backups; empty when unscheduled."`
	IncrementalBackupCron string    `json:"incrementalBackupCron" jsonschema:"Cron schedule for incremental backups; empty when unscheduled."`
	RetentionDays         int       `json:"retentionDays"`
	Disabled              bool      `json:"disabled"`
	UpdatedAt             time.Time `json:"updatedAt,omitzero"`
}

// backupListing is a database's backup policy and snapshots.
type backupListing struct {
	Policy  core.BackupPolicy
	Backups []backupView
}

// restoreResult is the database a restore created.
type restoreResult struct {
	DatabaseID string `json:"databaseId"`
	Name       string `json:"name"`
	Status     string `json:"status" jsonschema:"Always ready: the restored database is usable at once."`
}

// PolicyInput is a backup schedule a manager saves for one database.
type PolicyInput struct {
	FullBackupCron        string
	IncrementalBackupCron string
	RetentionDays         int
	Disabled              bool
}

// RestoreInput names the backup to restore and the database the restore
// creates; an empty Name derives one from the source.
type RestoreInput struct {
	Name     string
	BackupID string
}

func view(backup core.Backup) backupView {
	return backupView{ID: backup.ID, Status: backup.Status, CreatedAt: backup.CreatedAt, SizeBytes: backup.SizeBytes}
}

func policy(p core.BackupPolicy) policyView {
	return policyView(p)
}

// Service owns database backups, backup policies, and restores in a
// principal's selected project. The backups and settings pages and the MCP
// tools all call it.
type Service struct {
	deps core.Deps
}

func NewService(deps core.Deps) Service {
	return Service{deps: deps}
}

func (s Service) database(ctx context.Context, p core.Principal, databaseID string) (core.Project, core.Database, error) {
	if p.ProjectID == "" {
		return core.Project{}, core.Database{}, core.ErrNotFound
	}
	a, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Project{}, core.Database{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	if err != nil {
		return core.Project{}, core.Database{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok || db.ProjectID != project.ID {
		return core.Project{}, core.Database{}, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	return project, db, nil
}

// managedDatabase resolves a database the principal may manage.
func (s Service) managedDatabase(ctx context.Context, p core.Principal, databaseID string) (core.Database, error) {
	_, db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return core.Database{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return core.Database{}, err
	}
	if core.SystemDatabase(db) {
		return core.Database{}, core.ErrSystemReadOnly
	}
	return db, nil
}

// update commits mutate to the selected project once allow grants the
// principal's role in the selected organization.
func (s Service) update(ctx context.Context, p core.Principal, allow func(core.Organization, string) bool, mutate func(*core.Account, *core.Project) error) error {
	return s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !allow(org, p.UserID) {
			return core.ErrForbidden
		}
		if err := mutate(a, &project); err != nil {
			return err
		}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

// List returns a database's backups, newest first, with its policy.
func (s Service) List(ctx context.Context, p core.Principal, databaseID string) (backupListing, error) {
	project, db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return backupListing{}, err
	}
	listing := backupListing{Policy: project.BackupPolicies[db.ID], Backups: make([]backupView, 0, len(db.Backups))}
	for _, backup := range db.Backups {
		listing.Backups = append(listing.Backups, view(backup))
	}
	slices.SortFunc(listing.Backups, func(a, b backupView) int { return b.CreatedAt.Compare(a.CreatedAt) })
	return listing, nil
}

// Create snapshots a database's documents, schemas, and indexes.
func (s Service) Create(ctx context.Context, p core.Principal, databaseID string) (backupView, error) {
	db, err := s.managedDatabase(ctx, p, databaseID)
	if err != nil {
		return backupView{}, err
	}
	if !s.deps.LocalResources() {
		return backupView{}, ErrUnavailable
	}
	var created core.Backup
	err = s.update(ctx, p, core.Organization.CanManage, func(a *core.Account, project *core.Project) error {
		current, ok := project.Databases[db.ID]
		if !ok {
			return core.ErrNotFound
		}
		created = core.Backup{
			ID: core.NewID(), DatabaseID: db.ID, Status: "ready", CreatedAt: time.Now().UTC(),
			Documents: core.CloneDocuments(current.Documents), Schemas: core.CloneSchemas(current.Schemas), Indexes: core.CloneIndexes(current.Indexes),
		}
		for _, docs := range current.Documents {
			for _, body := range docs {
				created.SizeBytes += int64(len(body))
			}
		}
		current.Backups = append(current.Backups, created)
		project.Databases[db.ID] = current
		a.RecordAudit(p, "backup.created", current.Name, "database", created.CreatedAt)
		return nil
	})
	if err != nil {
		return backupView{}, err
	}
	return view(created), nil
}

// SetPolicy saves a database's backup schedule and retention.
func (s Service) SetPolicy(ctx context.Context, p core.Principal, databaseID string, in PolicyInput) (core.BackupPolicy, error) {
	db, err := s.managedDatabase(ctx, p, databaseID)
	if err != nil {
		return core.BackupPolicy{}, err
	}
	if !s.deps.LocalResources() {
		return core.BackupPolicy{}, ErrUnavailable
	}
	saved := core.BackupPolicy{
		FullBackupCron: normalizeCron(in.FullBackupCron), IncrementalBackupCron: normalizeCron(in.IncrementalBackupCron),
		RetentionDays: in.RetentionDays, Disabled: in.Disabled, UpdatedAt: time.Now().UTC(),
	}
	if saved.RetentionDays < 1 || saved.RetentionDays > maxRetentionDays {
		return core.BackupPolicy{}, errRetention
	}
	if !validCron(saved.FullBackupCron) || !validCron(saved.IncrementalBackupCron) {
		return core.BackupPolicy{}, errSchedule
	}
	err = s.update(ctx, p, core.Organization.CanManage, func(a *core.Account, project *core.Project) error {
		if _, ok := project.Databases[db.ID]; !ok {
			return core.ErrNotFound
		}
		if project.BackupPolicies == nil {
			project.BackupPolicies = make(map[string]core.BackupPolicy)
		}
		project.BackupPolicies[db.ID] = saved
		a.RecordAudit(p, "backup.policy_updated", db.Name, "database", saved.UpdatedAt)
		return nil
	})
	if err != nil {
		return core.BackupPolicy{}, err
	}
	return saved, nil
}

// Restore creates a new database from a backup of databaseID.
func (s Service) Restore(ctx context.Context, p core.Principal, databaseID string, in RestoreInput) (restoreResult, error) {
	db, err := s.managedDatabase(ctx, p, databaseID)
	if err != nil {
		return restoreResult{}, err
	}
	name := strings.TrimSpace(in.Name)
	if name != "" && !core.ValidDatabaseName(name) {
		return restoreResult{}, errRestoreName
	}
	target := core.Database{ID: core.NewID(), ProjectID: p.ProjectID, Region: db.Region, Regions: slices.Clone(db.Regions), Status: "ready", CreatedAt: time.Now().UTC()}
	err = s.update(ctx, p, core.Organization.CanProvisionResources, func(a *core.Account, project *core.Project) error {
		current, ok := project.Databases[db.ID]
		if !ok {
			return core.ErrNotFound
		}
		target.Name = name
		if target.Name == "" {
			target.Name = restoredName(*project, current.Name)
		}
		for _, existing := range project.Databases {
			if existing.Name == target.Name {
				return fmt.Errorf("%w: database %q already exists", core.ErrConflict, target.Name)
			}
		}
		index := slices.IndexFunc(current.Backups, func(b core.Backup) bool { return b.ID == in.BackupID && b.Status == "ready" })
		if index < 0 {
			return errBackupMissing
		}
		backup := current.Backups[index]
		target.Documents = core.CloneDocuments(backup.Documents)
		target.Schemas = core.CloneSchemas(backup.Schemas)
		target.Indexes = core.CloneIndexes(backup.Indexes)
		project.Databases[target.ID] = target
		a.RecordAudit(p, "backup.restored", current.Name+" → "+target.Name, "database", target.CreatedAt)
		return nil
	})
	if err != nil {
		return restoreResult{}, err
	}
	return restoreResult{DatabaseID: target.ID, Name: target.Name, Status: target.Status}, nil
}

// Overwrite replaces databaseID's documents, schemas, and indexes with those
// of its backup backupID once confirmName repeats the database name. Only the
// demo can: the live data plane restores into a new database only.
func (s Service) Overwrite(ctx context.Context, p core.Principal, databaseID, backupID, confirmName string) (restoreResult, error) {
	db, err := s.managedDatabase(ctx, p, databaseID)
	if err != nil {
		return restoreResult{}, err
	}
	if confirmName != db.Name {
		return restoreResult{}, errConfirmName
	}
	if !s.deps.LocalResources() {
		return restoreResult{}, ErrUnavailable
	}
	err = s.update(ctx, p, core.Organization.CanManage, func(a *core.Account, project *core.Project) error {
		current, ok := project.Databases[db.ID]
		if !ok {
			return core.ErrNotFound
		}
		index := slices.IndexFunc(current.Backups, func(b core.Backup) bool { return b.ID == backupID && b.Status == "ready" })
		if index < 0 {
			return errBackupMissing
		}
		backup := current.Backups[index]
		current.Documents = core.CloneDocuments(backup.Documents)
		current.Schemas = core.CloneSchemas(backup.Schemas)
		current.Indexes = core.CloneIndexes(backup.Indexes)
		project.Databases[db.ID] = current
		a.RecordAudit(p, "backup.overwritten", current.Name+" ← "+backup.CreatedAt.UTC().Format(time.RFC3339), "database", time.Now().UTC())
		return nil
	})
	if err != nil {
		return restoreResult{}, err
	}
	return restoreResult{DatabaseID: db.ID, Name: db.Name, Status: db.Status}, nil
}

// validCron accepts an empty schedule, which leaves that backup kind
// unscheduled, or five cron fields made of digits, ranges, lists, steps, and
// wildcards.
func validCron(schedule string) bool {
	if schedule == "" {
		return true
	}
	fields := strings.Fields(schedule)
	return len(fields) == 5 && !slices.ContainsFunc(fields, func(field string) bool {
		return strings.Trim(field, "0123456789*,-/") != "" || !strings.ContainsAny(field, "0123456789*")
	})
}

// normalizeCron collapses the whitespace between a schedule's fields.
func normalizeCron(schedule string) string {
	return strings.Join(strings.Fields(schedule), " ")
}

// restoredName derives an unused database name of at most 63 characters for
// a restore of source, shortening source to fit the suffix.
func restoredName(project core.Project, source string) string {
	taken := make(map[string]bool, len(project.Databases))
	for _, db := range project.Databases {
		taken[db.Name] = true
	}
	for attempt := 1; ; attempt++ {
		suffix := "-restore"
		if attempt > 1 {
			suffix += "-" + strconv.Itoa(attempt)
		}
		candidate := strings.TrimRight(source[:min(len(source), 63-len(suffix))], "-") + suffix
		if !taken[candidate] {
			return candidate
		}
	}
}
