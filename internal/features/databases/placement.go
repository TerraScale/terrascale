package databases

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/atlas"
	v1 "terrascale.tech/sdk/v1"
)

// TerraBase owns where a database lives (TS-643 placement contract v1, Ku
// TS-669). The first region of a placement is the database's home: it is
// created there and never moves. The others are replicas, which serve reads
// and writes and replicate with the home. The dashboard calls each region's
// TerraBase through ts-go with the signed-in user's ts-auth token.

var (
	// errHomeRegion refuses a placement without the database's home region.
	errHomeRegion = core.Invalid("a database stays in its home region; create a new database to move it")
	// errWrongRegion is a region that does not hold the database.
	errWrongRegion = fmt.Errorf("%w: a region outside the database's placement refused the request", core.ErrConflict)
	// errReplicated refuses deleting a database that still has replicas.
	errReplicated = fmt.Errorf("%w: remove the database's other regions before deleting it", core.ErrConflict)
	// errNoUserToken is a caller without a ts-auth token, such as MCP.
	errNoUserToken = fmt.Errorf("%w: database placement needs a dashboard sign-in", core.ErrDataPlane)
)

// placement checks regions against ts-atlas discovery and returns them in
// the order given; the first is the home region. One region keeps the
// database there; more replicate it between them.
func (s Service) placement(ctx context.Context, regions []string) ([]core.Region, error) {
	if s.deps.Regions == nil {
		return nil, errRegionsUnavailable
	}
	catalog, err := s.deps.Regions(ctx)
	if err != nil {
		return nil, fmt.Errorf("%w: %w", errRegionsUnavailable, err)
	}
	placed := make([]core.Region, 0, len(regions))
	for _, region := range regions {
		code := strings.ToUpper(strings.TrimSpace(region))
		i := slices.IndexFunc(catalog, func(r core.Region) bool { return r.Code == code })
		if i < 0 {
			return nil, core.Invalid(fmt.Sprintf("unknown region %q", region))
		}
		if slices.Contains(placed, catalog[i]) {
			return nil, core.Invalid(fmt.Sprintf("duplicate region %q", region))
		}
		placed = append(placed, catalog[i])
	}
	if len(placed) == 0 {
		return nil, core.Invalid("choose at least one region")
	}
	return placed, nil
}

// homeFirst moves the database's home region to the front of a new
// placement; a placement without it is refused.
func homeFirst(home string, placed []core.Region) ([]core.Region, error) {
	i := slices.IndexFunc(placed, func(r core.Region) bool { return r.Code == home })
	if home != "" && i < 0 {
		return nil, errHomeRegion
	}
	if i > 0 {
		placed = append([]core.Region{placed[i]}, slices.Delete(slices.Clone(placed), i, i+1)...)
	}
	return placed, nil
}

func codesOf(regions []core.Region) []string {
	codes := make([]string, len(regions))
	for i, region := range regions {
		codes[i] = region.Code
	}
	return codes
}

// replicaIDs are the Atlas ids of every region but the home.
func replicaIDs(placed []core.Region) []string {
	ids := make([]string, 0, len(placed)-1)
	for _, region := range placed[1:] {
		ids = append(ids, region.ID)
	}
	return ids
}

// regionClient is the signed-in user's TerraBase Management client in one
// region.
func regionClient(ctx context.Context, region core.Region) (*v1.ManagementUser, error) {
	token := core.UserToken(ctx)
	if token == "" {
		return nil, errNoUserToken
	}
	m, err := v1.NewManagement(v1.Config{BaseURL: region.API, HTTPClient: &http.Client{Timeout: 15 * time.Second}})
	if err != nil {
		return nil, core.ProviderError{Action: "database placement", Err: err}
	}
	return m.WithToken(token), nil
}

// register makes sure every region of a placement has the organization and
// project under their ids, which a region needs before it takes a database.
func register(ctx context.Context, placed []core.Region, org core.Organization, project core.Project) error {
	for _, region := range placed {
		client, err := regionClient(ctx, region)
		if err != nil {
			return err
		}
		if err := client.RegisterProject(ctx, v1.Organization{ID: org.ID, Name: org.Name}, v1.Project{ID: project.ID, Name: project.Name}); err != nil {
			return placementError(err)
		}
	}
	return nil
}

// createPlaced creates a database in its home region's TerraBase with its
// replica regions.
func createPlaced(ctx context.Context, org core.Organization, project core.Project, name string, placed []core.Region) (*v1.ManagedDatabase, error) {
	if err := register(ctx, placed, org, project); err != nil {
		return nil, err
	}
	home, err := regionClient(ctx, placed[0])
	if err != nil {
		return nil, err
	}
	managed, err := home.CreateDatabaseIn(ctx, org.ID, project.ID, name, v1.Placement{HomeRegion: placed[0].ID, ReplicaRegions: replicaIDs(placed)})
	return managed, placementError(err)
}

// dropPlaced removes a database createPlaced made when the dashboard could
// not record it: replicas first, as TerraBase requires.
func dropPlaced(ctx context.Context, org core.Organization, project core.Project, databaseID string, placed []core.Region) error {
	home, err := regionClient(ctx, placed[0])
	if err != nil {
		return err
	}
	if len(placed) > 1 {
		if _, err := home.SetDatabaseRegions(ctx, org.ID, project.ID, databaseID, nil); err != nil {
			return placementError(err)
		}
	}
	return placementError(home.DeleteDatabase(ctx, org.ID, project.ID, databaseID))
}

// setPlaced replaces a database's replica regions through its home region.
func setPlaced(ctx context.Context, org core.Organization, project core.Project, databaseID string, placed []core.Region) error {
	if err := register(ctx, placed[1:], org, project); err != nil {
		return err
	}
	home, err := regionClient(ctx, placed[0])
	if err != nil {
		return err
	}
	_, err = home.SetDatabaseRegions(ctx, org.ID, project.ID, databaseID, replicaIDs(placed))
	return placementError(err)
}

// placementError explains TerraBase's placement refusals in dashboard terms.
func placementError(err error) error {
	var apiErr *v1.APIError
	switch {
	case err == nil:
		return nil
	case errors.Is(err, v1.ErrWrongRegion) && errors.As(err, &apiErr):
		codes := make([]string, len(apiErr.Regions))
		for i, id := range apiErr.Regions {
			codes[i] = atlas.Code(id)
		}
		return fmt.Errorf("%w; it lives in %s", errWrongRegion, strings.Join(codes, ", "))
	case errors.Is(err, v1.ErrDatabaseReplicated):
		return errReplicated
	case errors.Is(err, v1.ErrNameTaken):
		return fmt.Errorf("%w: database name is taken", core.ErrConflict)
	case errors.Is(err, v1.ErrUnknownRoute), errors.Is(err, v1.ErrNotFound):
		return fmt.Errorf("%w: database", core.ErrNotFound)
	case errors.Is(err, v1.ErrForbidden):
		return core.ErrForbidden
	case errors.Is(err, v1.ErrInvalidRequest) && errors.As(err, &apiErr):
		return core.Invalid(apiErr.Message)
	default:
		return core.ProviderError{Action: "database placement", Err: err}
	}
}
