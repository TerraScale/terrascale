package databases

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type databaseDetail struct {
	databaseView
	Endpoint            string   `json:"endpoint,omitempty"`
	Regions             []string `json:"regions"`
	Collections         int      `json:"collections"`
	Documents           int      `json:"documents"`
	Bytes               int64    `json:"bytes" jsonschema:"Total JSON bytes of the documents."`
	Unavailable         string   `json:"unavailable,omitempty" jsonschema:"Why a system database's counts are missing."`
	BackupsEnabled      bool     `json:"backupsEnabled"`
	BackupRetentionDays int      `json:"backupRetentionDays,omitempty"`
	CanManage           bool     `json:"canManage" jsonschema:"Whether the caller may branch, back up, or delete this database."`
	CanBranch           bool     `json:"canBranch"`
}

type databaseList struct {
	Databases []databaseView `json:"databases"`
}

type databaseInput struct {
	core.Scope
	DatabaseID string `json:"databaseId" jsonschema:"Database ID from list_databases."`
}

type createDatabaseInput struct {
	core.Scope
	Name    string   `json:"name" jsonschema:"Database name: 1-63 lowercase letters, digits, and inner hyphens."`
	Region  string   `json:"region,omitempty" jsonschema:"Region code such as GRU or SFO. Defaults to the project's primary region."`
	Regions []string `json:"regions,omitempty" jsonschema:"Placement regions for one database, such as GRU and SFO, which replicate between them. Takes precedence over region."`
}

type setDatabaseRegionsInput struct {
	core.Scope
	DatabaseID string   `json:"databaseId" jsonschema:"Database ID from list_databases."`
	Regions    []string `json:"regions" jsonschema:"Region codes such as GRU or SFO, the complete new list. It must include the database's home region (the one it was created in), which never changes; the others replicate it."`
}

type branchDatabaseInput struct {
	core.Scope
	DatabaseID string `json:"databaseId" jsonschema:"ID of the ready database to copy."`
	Name       string `json:"name,omitempty" jsonschema:"Branch database name. Defaults to <source>-preview-branch."`
}

type deleteDatabaseInput struct {
	core.Scope
	DatabaseID  string `json:"databaseId" jsonschema:"Database ID to delete."`
	ConfirmName string `json:"confirmName" jsonschema:"The database's exact name, confirming permanent deletion."`
}

type deleted struct {
	Deleted bool `json:"deleted"`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_databases", Title: "List databases", ReadOnly: true,
		Description: "List the databases in a project with their status and region.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (databaseList, error) {
		list, err := service.List(ctx, p)
		return databaseList{Databases: list}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_database", Title: "Get database", ReadOnly: true,
		Description: "Show one database's status, regions, storage totals, and backup state.",
	}, func(ctx context.Context, p core.Principal, in databaseInput) (databaseDetail, error) {
		return service.Detail(ctx, p, in.DatabaseID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_database", Title: "Create database",
		Description: "Provision a new database in a project. Requires an owner or admin role and a plan with database capacity.",
	}, func(ctx context.Context, p core.Principal, in createDatabaseInput) (databaseView, error) {
		if len(in.Regions) > 0 {
			return service.Create(ctx, p, in.Name, in.Regions...)
		}
		return service.Create(ctx, p, in.Name, in.Region)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "set_database_regions", Title: "Set database regions",
		Description: "Choose where a database lives: its home region alone, or with replica regions that replicate it. The home region never changes. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in setDatabaseRegionsInput) (databaseView, error) {
		return service.SetRegions(ctx, p, in.DatabaseID, in.Regions...)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "branch_database", Title: "Branch database",
		Description: "Create a new database that copies a ready database's documents, schemas, and indexes.",
	}, func(ctx context.Context, p core.Principal, in branchDatabaseInput) (databaseView, error) {
		return service.Branch(ctx, p, in.DatabaseID, in.Name)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "delete_database", Title: "Delete database", Destructive: true,
		Description: "Permanently delete a database and all of its data. Requires confirmName to equal the database name.",
	}, func(ctx context.Context, p core.Principal, in deleteDatabaseInput) (deleted, error) {
		err := service.Delete(ctx, p, in.DatabaseID, in.ConfirmName)
		return deleted{Deleted: err == nil}, err
	})
}
