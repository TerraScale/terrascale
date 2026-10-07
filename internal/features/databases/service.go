package databases

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

var (
	errInvalidName = core.Invalid("database names use 1-63 lowercase letters, digits, and inner hyphens")
	errNotReady    = fmt.Errorf("%w: only a ready database can be branched", core.ErrConflict)
	errConfirmName = core.Invalid("type the database name to confirm deletion")
	// errRegionsUnavailable is a placement that could not be checked because
	// ts-atlas region discovery did not answer.
	errRegionsUnavailable = fmt.Errorf("%w: region discovery is unavailable", core.ErrDataPlane)
)

// Service owns the database lifecycle in a principal's selected project.
// HTTP handlers and MCP tools both call it.
type Service struct {
	deps core.Deps
}

func (s Service) selected(ctx context.Context, p core.Principal) (core.Account, core.Project, error) {
	if p.ProjectID == "" {
		return core.Account{}, core.Project{}, core.ErrNotFound
	}
	a, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Account{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return a, project, err
}

// List returns the project's databases by name.
func (s Service) List(ctx context.Context, p core.Principal) ([]databaseView, error) {
	_, project, err := s.selected(ctx, p)
	if err != nil {
		return nil, err
	}
	list := make([]databaseView, 0, len(project.Databases))
	for _, db := range project.Databases {
		list = append(list, view(db))
	}
	slices.SortFunc(list, func(a, b databaseView) int { return strings.Compare(a.Name, b.Name) })
	return list, nil
}

// Detail returns one database with its storage, region, and backup summary.
func (s Service) Detail(ctx context.Context, p core.Principal, databaseID string) (databaseDetail, error) {
	a, project, err := s.selected(ctx, p)
	if err != nil {
		return databaseDetail{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok {
		return databaseDetail{}, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	org := a.Organizations[p.OrganizationID]
	canManage := org.CanManage(p.UserID) && !core.SystemDatabase(db)
	info := makeDetailInfo(s.deps.LocalResources(), p, a, project, db, canManage, canManage && org.CanProvisionResources(p.UserID)).withContents(ctx, s.deps, p, db)
	detail := databaseDetail{
		databaseView: view(db), Endpoint: db.Endpoint, Collections: info.Collections, Documents: info.Documents, Bytes: info.Bytes, Unavailable: info.Unavailable,
		BackupsEnabled: info.BackupsEnabled, BackupRetentionDays: db.BackupRetentionDays, CanManage: canManage, CanBranch: info.CanBranch,
	}
	for _, region := range info.Regions {
		detail.Regions = append(detail.Regions, region.Name)
	}
	return detail, nil
}

// Create provisions a database across the selected regions. No region uses
// the project's primary region for callers that omit placement.
func (s Service) Create(ctx context.Context, p core.Principal, name string, regions ...string) (databaseView, error) {
	a, project, err := s.selected(ctx, p)
	if err != nil {
		return databaseView{}, err
	}
	name = strings.TrimSpace(name)
	if !core.ValidDatabaseName(name) {
		return databaseView{}, errInvalidName
	}
	if len(regions) == 0 || len(regions) == 1 && regions[0] == "" {
		regions = []string{project.Region}
	}
	placed, err := s.placement(ctx, regions)
	if err != nil {
		return databaseView{}, err
	}
	codes := codesOf(placed)
	db := core.Database{
		ID: core.NewID("db"), ProjectID: p.ProjectID, Name: name, Region: codes[0], Regions: codes, Status: "ready",
		CreatedAt: time.Now().UTC(), Documents: make(map[string]map[string][]byte),
	}
	org := a.Organizations[p.OrganizationID]
	if !s.deps.LocalResources() {
		if !org.CanProvisionResources(p.UserID) {
			return databaseView{}, core.ErrForbidden
		}
		managed, err := createPlaced(ctx, org, project, name, placed)
		if err != nil {
			return databaseView{}, err
		}
		db.ID, db.CreatedAt, db.Documents, db.Provisioned = managed.ID, managed.CreatedAt, nil, true
	}
	// The store refuses the commit with core.ErrDatabaseLimit when the plan's
	// database allowance is used up.
	err = s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !org.CanProvisionResources(p.UserID) {
			return core.ErrForbidden
		}
		for _, existing := range project.Databases {
			if existing.Name == name {
				return fmt.Errorf("%w: database %q already exists", core.ErrConflict, name)
			}
		}
		if project.Databases == nil {
			project.Databases = make(map[string]core.Database)
		}
		project.Databases[db.ID] = db
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
	if err != nil {
		if !s.deps.LocalResources() {
			// ponytail: best-effort cleanup; a failed drop leaves the database
			// in TerraBase, unrecorded, until a Management commit creates both
			// together (TS-671).
			if dropErr := dropPlaced(ctx, org, project, db.ID, placed); dropErr != nil {
				err = errors.Join(err, dropErr)
			}
		}
		return databaseView{}, err
	}
	return view(db), nil
}

// SetRegions changes where a database lives: its home region alone, or with
// replicas. The home region never changes.
func (s Service) SetRegions(ctx context.Context, p core.Principal, databaseID string, regions ...string) (databaseView, error) {
	a, project, err := s.selected(ctx, p)
	if err != nil {
		return databaseView{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return databaseView{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok {
		return databaseView{}, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	if core.SystemDatabase(db) {
		return databaseView{}, core.ErrSystemReadOnly
	}
	placed, err := s.placement(ctx, regions)
	if err != nil {
		return databaseView{}, err
	}
	if placed, err = homeFirst(db.Region, placed); err != nil {
		return databaseView{}, err
	}
	if !s.deps.LocalResources() {
		if err := setPlaced(ctx, a.Organizations[p.OrganizationID], project, db.ID, placed); err != nil {
			return databaseView{}, err
		}
	}
	codes := codesOf(placed)
	err = s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !org.CanManage(p.UserID) {
			return core.ErrForbidden
		}
		current, ok := project.Databases[databaseID]
		if !ok {
			return fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
		}
		current.Region, current.Regions = codes[0], codes
		project.Databases[databaseID] = current
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		db = current
		return nil
	})
	if err != nil {
		return databaseView{}, err
	}
	return view(db), nil
}

// Branch copies a ready database into a new database in the same project.
// An empty name derives a preview branch name from the source.
func (s Service) Branch(ctx context.Context, p core.Principal, sourceID, name string) (databaseView, error) {
	_, project, err := s.selected(ctx, p)
	if err != nil {
		return databaseView{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return databaseView{}, err
	}
	source, ok := project.Databases[sourceID]
	if !ok {
		return databaseView{}, fmt.Errorf("%w: database %q", core.ErrNotFound, sourceID)
	}
	if core.SystemDatabase(source) {
		return databaseView{}, core.ErrSystemReadOnly
	}
	name = strings.TrimSpace(name)
	if name == "" {
		name = previewBranchName(source.Name)
	}
	if !core.ValidDatabaseName(name) {
		return databaseView{}, errInvalidName
	}
	if source.Status != "ready" {
		return databaseView{}, errNotReady
	}
	branch := core.Database{ID: core.NewID("db"), ProjectID: project.ID, Name: name, Region: source.Region, Regions: slices.Clone(databaseRegions(source)), Status: "ready", CreatedAt: time.Now().UTC()}
	err = s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !org.CanProvisionResources(p.UserID) {
			return core.ErrForbidden
		}
		current, ok := project.Databases[source.ID]
		if !ok {
			return fmt.Errorf("%w: database %q", core.ErrNotFound, source.ID)
		}
		if current.Status != "ready" {
			return errNotReady
		}
		for _, db := range project.Databases {
			if db.Name == name {
				return fmt.Errorf("%w: database %q already exists", core.ErrConflict, name)
			}
		}
		if s.deps.LocalResources() {
			branch.Documents = core.CloneDocuments(current.Documents)
			branch.Schemas = core.CloneSchemas(current.Schemas)
			branch.Indexes = core.CloneIndexes(current.Indexes)
			branch.DocumentHistory = core.CloneHistory(current.DocumentHistory)
		}
		project.Databases[branch.ID] = branch
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
	if err != nil {
		return databaseView{}, err
	}
	return view(branch), nil
}

// Delete removes a database once confirmName repeats its name.
func (s Service) Delete(ctx context.Context, p core.Principal, databaseID, confirmName string) error {
	_, project, err := s.selected(ctx, p)
	if err != nil {
		return err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return err
	}
	db, ok := project.Databases[databaseID]
	if !ok {
		return fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	if core.SystemDatabase(db) {
		return core.ErrSystemReadOnly
	}
	if confirmName != db.Name {
		return errConfirmName
	}
	return s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
			return core.ErrForbidden
		}
		if _, ok := project.Databases[db.ID]; !ok {
			return core.ErrNotFound
		}
		project.RemoveDatabase(db.ID, time.Now().UTC())
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

func previewBranchName(sourceName string) string {
	const suffix = "-preview-branch"
	const maxNameLength = 63
	if len(sourceName) > maxNameLength-len(suffix) {
		sourceName = strings.TrimRight(sourceName[:maxNameLength-len(suffix)], "-")
	}
	return sourceName + suffix
}
