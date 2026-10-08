package core

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"slices"

	"terrascale.tech/sdk/v1"
)

// ErrDataPlane marks a live data plane that is not configured or reachable.
var ErrDataPlane = errors.New("live data plane unavailable")

// ErrNoUserToken is a caller without the signed-in user's ts-auth access
// token, such as MCP or the platform API, which present a dashboard token
// TerraBase does not accept.
var ErrNoUserToken = fmt.Errorf("%w: this TerraBase request needs a dashboard sign-in", ErrDataPlane)

// ProviderError is a data plane request that failed after the dashboard
// accepted it.
type ProviderError struct {
	Action string
	Err    error
}

func (e ProviderError) Error() string { return e.Action + " failed: " + e.Err.Error() }
func (e ProviderError) Unwrap() error { return e.Err }

// dataKeyName names the data key the dashboard keeps for each TerraBase
// project and region.
const dataKeyName = "ts-dashboard"

// Every database outside the demo lives in TerraBase (TS-671), in the
// TerraBase project TerraBaseIDs names, placed in regions (TS-669). The
// dashboard reaches each region's TerraBase through ts-go: Management as the
// signed-in user with their ts-auth access token, and the Documents API with
// the project's data key in that region.

// Region returns the region of a dashboard region code in ts-atlas discovery.
func (d Deps) Region(ctx context.Context, code string) (Region, error) {
	return d.region(ctx, func(r Region) bool { return r.Code == code })
}

func (d Deps) region(ctx context.Context, match func(Region) bool) (Region, error) {
	if d.Regions == nil {
		return Region{}, fmt.Errorf("%w: region discovery is not configured", ErrDataPlane)
	}
	catalog, err := d.Regions(ctx)
	if err != nil {
		return Region{}, fmt.Errorf("%w: region discovery: %w", ErrDataPlane, err)
	}
	if i := slices.IndexFunc(catalog, match); i >= 0 {
		return catalog[i], nil
	}
	return Region{}, fmt.Errorf("%w: region discovery does not list the region", ErrDataPlane)
}

// placed is the regions a database is recorded in, its home first.
func (d Deps) placed(ctx context.Context, project Project, db Database) ([]Region, error) {
	codes := db.Regions
	if len(codes) == 0 {
		codes = []string{cmp.Or(db.Region, project.Region)}
	}
	regions := make([]Region, 0, len(codes))
	for _, code := range codes {
		region, err := d.Region(ctx, code)
		if err != nil {
			return nil, err
		}
		regions = append(regions, region)
	}
	return regions, nil
}

// Home is a database's home region, where TerraBase manages it.
func (d Deps) Home(ctx context.Context, project Project, db Database) (Region, error) {
	placed, err := d.placed(ctx, project, db)
	if err != nil {
		return Region{}, err
	}
	return placed[0], nil
}

// Manage returns a region's TerraBase Management API acting as the signed-in
// user.
func (d Deps) Manage(ctx context.Context, region Region) (*v1.ManagementUser, error) {
	token := UserToken(ctx)
	if token == "" {
		return nil, ErrNoUserToken
	}
	if d.SDK == nil {
		return nil, ErrDataPlane
	}
	m, err := d.SDK.Management(region.API)
	if err != nil {
		return nil, ProviderError{Action: "TerraBase Management", Err: err}
	}
	return m.WithToken(token), nil
}

// register makes sure each region has the project's TerraBase organization
// and project under TerraBaseIDs. Whoever registers them owns them in
// TerraBase, so only a project admin may.
func (d Deps) register(ctx context.Context, p Principal, project Project, regions ...Region) error {
	// A project mirrored from TerraBase is a service's own (TS-611).
	if project.System {
		return ErrSystemReadOnly
	}
	if len(regions) == 0 {
		return nil
	}
	if err := Can(ctx, d.Store, p, Organization.CanManage); err != nil {
		return fmt.Errorf("%w: a project admin has to open this project's databases first", err)
	}
	org, proj := project.TerraBaseIDs(p.OrganizationID)
	for _, region := range regions {
		m, err := d.Manage(ctx, region)
		if err != nil {
			return err
		}
		// TerraBase names need not be unique; the dashboard's IDs say which
		// organization this is.
		if err := m.RegisterProject(ctx, v1.Organization{ID: org, Name: p.OrganizationID}, v1.Project{ID: proj, Name: project.Name}); err != nil {
			return ProviderError{Action: "TerraBase project registration", Err: err}
		}
	}
	return nil
}

// dataKey returns the dashboard's data key of the project in a region,
// issuing and recording it the first time.
func (d Deps) dataKey(ctx context.Context, p Principal, project Project, region Region) (DataKey, error) {
	if key, ok := project.DataKeys[region.ID]; ok {
		return key, nil
	}
	if err := d.register(ctx, p, project, region); err != nil {
		return DataKey{}, err
	}
	m, err := d.Manage(ctx, region)
	if err != nil {
		return DataKey{}, err
	}
	org, proj := project.TerraBaseIDs(p.OrganizationID)
	created, err := m.CreateProjectAPIKey(ctx, org, proj, v1.ProjectAPIKeyRequest{
		Name: dataKeyName, Scopes: []string{"documents:read", "documents:write"},
	})
	if err != nil {
		return DataKey{}, ProviderError{Action: "TerraBase data key", Err: err}
	}
	key := DataKey{ID: created.Key.ID, Secret: created.Secret}
	var recorded DataKey
	err = d.Store.UpdateAs(context.WithoutCancel(ctx), p, func(a *Account) error {
		current, ok := a.Organizations[p.OrganizationID].Projects[project.ID]
		if !ok {
			return ErrNotFound
		}
		if current.DataKeys == nil {
			current.DataKeys = make(map[string]DataKey)
		}
		if _, ok := current.DataKeys[region.ID]; !ok {
			current.DataKeys[region.ID] = key
			a.Organizations[p.OrganizationID].Projects[project.ID] = current
		}
		recorded = current.DataKeys[region.ID]
		return nil
	})
	if err != nil {
		return DataKey{}, err
	}
	if recorded.ID != key.ID {
		// A concurrent first use recorded its key first.
		_ = m.RevokeProjectAPIKey(context.WithoutCancel(ctx), org, proj, key.ID)
	}
	return recorded, nil
}

// DataKeyRegions are the regions the dashboard holds a data key of the
// project in: where the project is registered.
func (d Deps) DataKeyRegions(ctx context.Context, project Project) ([]Region, error) {
	regions := make([]Region, 0, len(project.DataKeys))
	for id := range project.DataKeys {
		region, err := d.region(ctx, func(r Region) bool { return r.ID == id })
		if err != nil {
			return nil, err
		}
		regions = append(regions, region)
	}
	return regions, nil
}

// Data returns the Documents API handle of a database (Ensure) in its home
// region, with the project's data key there.
func (d Deps) Data(ctx context.Context, p Principal, project Project, db Database) (*v1.Database, error) {
	if SystemDatabase(db) {
		return nil, ErrSystemReadOnly
	}
	placed, err := d.placed(ctx, project, db)
	if err != nil {
		return nil, err
	}
	key, err := d.dataKey(ctx, p, project, placed[0])
	if err != nil {
		return nil, err
	}
	client, err := d.SDK.Documents(placed[0].API, key.Secret)
	if err != nil {
		return nil, fmt.Errorf("%w: %w", ErrDataPlane, err)
	}
	_, proj := project.TerraBaseIDs(p.OrganizationID)
	return client.Database(proj, db.Name), nil
}

// ManageDatabase returns a database (Ensure) with its home region's
// Management API acting as the signed-in user, and its project's TerraBase
// IDs.
func (d Deps) ManageDatabase(ctx context.Context, p Principal, project Project, db Database) (m *v1.ManagementUser, org, proj string, _ Database, err error) {
	if SystemDatabase(db) {
		return nil, "", "", Database{}, ErrSystemReadOnly
	}
	placed, err := d.placed(ctx, project, db)
	if err != nil {
		return nil, "", "", Database{}, err
	}
	if m, err = d.Manage(ctx, placed[0]); err != nil {
		return nil, "", "", Database{}, err
	}
	org, proj = project.TerraBaseIDs(p.OrganizationID)
	return m, org, proj, db, nil
}

// CreateDatabase creates the database name in TerraBase: in its home region,
// placed[0], with the others as replicas, after registering the project in
// each. A database of that name the dashboard created but did not record,
// such as when its commit failed, is returned instead.
func (d Deps) CreateDatabase(ctx context.Context, p Principal, project Project, name string, placed []Region) (string, error) {
	if err := d.register(ctx, p, project, placed...); err != nil {
		return "", err
	}
	m, err := d.Manage(ctx, placed[0])
	if err != nil {
		return "", err
	}
	org, proj := project.TerraBaseIDs(p.OrganizationID)
	created, err := m.CreateDatabaseIn(ctx, org, proj, name, v1.Placement{HomeRegion: placed[0].ID, ReplicaRegions: replicaIDs(placed)})
	if errors.Is(err, v1.ErrNameTaken) {
		return existingDatabase(ctx, m, org, proj, name)
	}
	if err != nil {
		return "", ProviderError{Action: "database create", Err: err}
	}
	return created.ID, nil
}

func existingDatabase(ctx context.Context, m *v1.ManagementUser, org, proj, name string) (string, error) {
	for opts := (v1.PageOptions{Limit: 100}); ; {
		page, err := m.Databases(ctx, org, proj, opts)
		if err != nil {
			return "", ProviderError{Action: "database list", Err: err}
		}
		if i := slices.IndexFunc(page.Items, func(db v1.ManagedDatabase) bool { return db.Name == name }); i >= 0 {
			return page.Items[i].ID, nil
		}
		if opts.After = page.Next; opts.After == "" {
			return "", fmt.Errorf("%w: database %q", ErrConflict, name)
		}
	}
}

// replicaIDs are the Atlas IDs of every region of a placement but the home.
func replicaIDs(placed []Region) []string {
	ids := make([]string, 0, len(placed)-1)
	for _, region := range placed[1:] {
		ids = append(ids, region.ID)
	}
	return ids
}

// SetDatabaseRegions replaces replicas through the database's home region.
func (d Deps) SetDatabaseRegions(ctx context.Context, p Principal, project Project, db Database, placed []Region) error {
	if db.ID == "" {
		return nil
	}
	if err := d.register(ctx, p, project, placed[1:]...); err != nil {
		return err
	}
	m, err := d.Manage(ctx, placed[0])
	if err != nil {
		return err
	}
	org, proj := project.TerraBaseIDs(p.OrganizationID)
	if _, err := m.SetDatabaseRegions(ctx, org, proj, db.ID, replicaIDs(placed)); err != nil {
		return ProviderError{Action: "database placement", Err: err}
	}
	return nil
}

// BranchDatabase copies a database into the database name in its home
// region and returns the copy's TerraBase ID.
func (d Deps) BranchDatabase(ctx context.Context, p Principal, project Project, source Database, name string) (string, error) {
	m, org, proj, source, err := d.ManageDatabase(ctx, p, project, source)
	if err != nil {
		return "", err
	}
	created, err := m.BranchDatabase(ctx, org, proj, source.ID, name)
	if err != nil {
		return "", ProviderError{Action: "database branch", Err: err}
	}
	return created.ID, nil
}

// DeleteDatabase deletes a database through its home region; one never used
// in TerraBase has nothing there.
func (d Deps) DeleteDatabase(ctx context.Context, p Principal, project Project, db Database) error {
	if db.ID == "" {
		return nil
	}
	m, org, proj, db, err := d.ManageDatabase(ctx, p, project, db)
	if err != nil {
		return err
	}
	if err := m.DeleteDatabase(ctx, org, proj, db.ID); err != nil && !errors.Is(err, v1.ErrNotFound) {
		return ProviderError{Action: "database delete", Err: err}
	}
	return nil
}

// ManageProject returns a region's Management API acting as the signed-in
// user, after making sure the project is registered there with the
// dashboard's data key, and the project's TerraBase IDs.
func (d Deps) ManageProject(ctx context.Context, p Principal, project Project, region Region) (m *v1.ManagementUser, org, proj string, err error) {
	if _, err = d.dataKey(ctx, p, project, region); err != nil {
		return nil, "", "", err
	}
	if m, err = d.Manage(ctx, region); err != nil {
		return nil, "", "", err
	}
	org, proj = project.TerraBaseIDs(p.OrganizationID)
	return m, org, proj, nil
}
