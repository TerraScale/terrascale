package backups

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type databaseInput struct {
	core.Scope
	DatabaseID string `json:"databaseId" jsonschema:"Database ID from list_databases."`
}

type setPolicyInput struct {
	databaseInput
	FullBackupCron        string `json:"fullBackupCron" jsonschema:"Five-field cron schedule for full backups, for example 0 3 * * *."`
	IncrementalBackupCron string `json:"incrementalBackupCron" jsonschema:"Five-field cron schedule for incremental backups, for example 0 */6 * * *."`
	RetentionDays         int    `json:"retentionDays" jsonschema:"Days to keep backups, 1 to 35."`
	Disabled              bool   `json:"disabled,omitempty" jsonschema:"Save the schedule but pause scheduled backups."`
}

type restoreBackupInput struct {
	databaseInput
	Name     string `json:"name,omitempty" jsonschema:"Name of the new database the restore creates. Defaults to <source>-restore."`
	BackupID string `json:"backupId,omitempty" jsonschema:"Backup ID from list_backups to restore. Required in the in-memory demo."`
	AtMillis *int64 `json:"atMillis,omitempty" jsonschema:"Live point-in-time restore target in Unix milliseconds. Defaults to the latest state."`
}

type backupList struct {
	Policy    *policyView  `json:"policy,omitempty" jsonschema:"The saved backup schedule; absent on the live data plane."`
	Backups   []backupView `json:"backups" jsonschema:"Backups, newest first."`
	Readiness any          `json:"readiness,omitempty" jsonschema:"Live data plane backup readiness."`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_backups", Title: "List backups", ReadOnly: true,
		Description: "List a database's backups and its backup policy.",
	}, func(ctx context.Context, p core.Principal, in databaseInput) (backupList, error) {
		listing, err := service.List(ctx, p, in.DatabaseID)
		if err != nil {
			return backupList{}, err
		}
		if listing.Readiness != nil {
			return backupList{Backups: []backupView{}, Readiness: listing.Readiness}, nil
		}
		saved := policy(listing.Policy)
		return backupList{Policy: &saved, Backups: listing.Backups}, nil
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_backup", Title: "Create backup",
		Description: "Take an on-demand backup of a database's documents, schemas, and indexes. Requires an owner or admin role.",
	}, func(_ context.Context, p core.Principal, in databaseInput) (backupView, error) {
		return service.Create(p, in.DatabaseID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "set_backup_policy", Title: "Set backup policy", Idempotent: true,
		Description: "Save a database's backup schedule and retention. Requires an owner or admin role.",
	}, func(_ context.Context, p core.Principal, in setPolicyInput) (policyView, error) {
		saved, err := service.SetPolicy(p, in.DatabaseID, PolicyInput{
			FullBackupCron: in.FullBackupCron, IncrementalBackupCron: in.IncrementalBackupCron,
			RetentionDays: in.RetentionDays, Disabled: in.Disabled,
		})
		return policy(saved), err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "restore_backup", Title: "Restore backup",
		Description: "Restore a database backup into a new database; the source database is unchanged. Requires an owner or admin role and provisioning rights.",
	}, func(ctx context.Context, p core.Principal, in restoreBackupInput) (restoreResult, error) {
		return service.Restore(ctx, p, in.DatabaseID, RestoreInput{Name: in.Name, BackupID: in.BackupID, AtMillis: in.AtMillis})
	})
}
