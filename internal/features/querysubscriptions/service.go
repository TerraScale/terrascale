package querysubscriptions

import (
	"context"
	"errors"
	"fmt"
	"regexp"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

var (
	viewNamePattern  = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,63}$`)
	demoQueryPattern = regexp.MustCompile(`(?i)^select\s+\*\s+from\s+[a-z][a-z0-9_-]*(?:\s+limit\s+(?:[1-9]|[1-9][0-9]|100))?\s*;?$`)
	errLive          = errors.New("live shared-result configuration is unavailable")
)

type subscriptionView struct {
	ID           string    `json:"id"`
	DatabaseID   string    `json:"databaseId"`
	DatabaseName string    `json:"databaseName,omitempty"`
	Name         string    `json:"name"`
	Query        string    `json:"query"`
	Transport    string    `json:"transport" jsonschema:"http or websocket."`
	Status       string    `json:"status" jsonschema:"saved or paused."`
	CreatedAt    time.Time `json:"createdAt"`
}

// attachInput is a shared-result configuration to save for a database.
type attachInput struct {
	DatabaseID string
	Name       string
	Query      string
	Transport  string
}

// Service owns the selected project's shared-result configurations. HTTP
// handlers and MCP tools both call it; it never starts a live stream.
type Service struct {
	deps core.Deps
}

func (s Service) project(ctx context.Context, p core.Principal) (core.Project, error) {
	if p.ProjectID == "" {
		return core.Project{}, core.ErrNotFound
	}
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Project{}, err
	}
	return account.ProjectFor(p, p.OrganizationID, p.ProjectID)
}

// update commits mutate to the selected project once the principal may write
// in the selected organization. Only the in-memory demo stores configurations.
func (s Service) update(ctx context.Context, p core.Principal, mutate func(*core.Project) error) error {
	if !s.deps.LocalResources() {
		return errLive
	}
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := account.Organizations[p.OrganizationID]
		if !org.CanWrite(p.UserID) {
			return core.ErrForbidden
		}
		if err := mutate(&project); err != nil {
			return err
		}
		org.Projects[p.ProjectID] = project
		account.Organizations[p.OrganizationID] = org
		return nil
	})
}

// List returns the project's configurations by name.
func (s Service) List(ctx context.Context, p core.Principal) ([]subscriptionView, error) {
	project, err := s.project(ctx, p)
	if err != nil {
		return nil, err
	}
	views := make([]subscriptionView, 0, len(project.SharedResultConfigs))
	for _, config := range project.SharedResultConfigs {
		views = append(views, subscriptionView{
			ID: config.ID, DatabaseID: config.DatabaseID, DatabaseName: project.Databases[config.DatabaseID].Name,
			Name: config.Name, Query: config.Query, Transport: config.Transport, Status: config.Status, CreatedAt: config.CreatedAt,
		})
	}
	slices.SortFunc(views, func(a, b subscriptionView) int { return strings.Compare(a.Name, b.Name) })
	return views, nil
}

// Attach saves a shared-result configuration for one of the project's
// databases.
func (s Service) Attach(ctx context.Context, p core.Principal, in attachInput) (subscriptionView, error) {
	if !s.deps.LocalResources() {
		return subscriptionView{}, errLive
	}
	name, query := strings.TrimSpace(in.Name), strings.TrimSpace(in.Query)
	if !viewNamePattern.MatchString(name) {
		return subscriptionView{}, core.Invalid("Name the view with 1 to 64 lowercase letters, digits, underscores, and hyphens, starting with a letter.")
	}
	if len(query) > 4096 || !demoQueryPattern.MatchString(query) || sdk.ValidateSQL(query, nil) != nil {
		return subscriptionView{}, core.Invalid("The demo shares SELECT * FROM collection with an optional LIMIT of 1 to 100.")
	}
	if in.Transport != "http" && in.Transport != "websocket" {
		return subscriptionView{}, core.Invalid("Choose HTTP + CDN or WebSocket delivery.")
	}
	config := core.SharedResultConfig{
		ID: core.NewID("cfg"), DatabaseID: in.DatabaseID, Name: name, Query: query,
		Transport: in.Transport, Status: "saved", CreatedAt: time.Now().UTC(),
	}
	var databaseName string
	err := s.update(ctx, p, func(project *core.Project) error {
		db, ok := project.Databases[in.DatabaseID]
		if !ok || db.ProjectID != project.ID {
			return core.Invalid("Choose a database in this project.")
		}
		databaseName = db.Name
		for _, existing := range project.SharedResultConfigs {
			if existing.Name == name {
				return fmt.Errorf("%w: %q already exists", core.ErrConflict, name)
			}
		}
		if project.SharedResultConfigs == nil {
			project.SharedResultConfigs = make(map[string]core.SharedResultConfig)
		}
		project.SharedResultConfigs[config.ID] = config
		return nil
	})
	if err != nil {
		return subscriptionView{}, err
	}
	return subscriptionView{
		ID: config.ID, DatabaseID: config.DatabaseID, DatabaseName: databaseName, Name: config.Name,
		Query: config.Query, Transport: config.Transport, Status: config.Status, CreatedAt: config.CreatedAt,
	}, nil
}

// Pause marks a configuration paused.
func (s Service) Pause(ctx context.Context, p core.Principal, id string) error {
	return s.setStatus(ctx, p, id, "paused")
}

// Resume saves a paused configuration again.
func (s Service) Resume(ctx context.Context, p core.Principal, id string) error {
	return s.setStatus(ctx, p, id, "saved")
}

func (s Service) setStatus(ctx context.Context, p core.Principal, id, status string) error {
	return s.update(ctx, p, func(project *core.Project) error {
		config, ok := project.SharedResultConfigs[id]
		if !ok {
			return fmt.Errorf("%w: configuration %q", core.ErrNotFound, id)
		}
		config.Status = status
		project.SharedResultConfigs[id] = config
		return nil
	})
}

// Detach deletes a configuration.
func (s Service) Detach(ctx context.Context, p core.Principal, id string) error {
	return s.update(ctx, p, func(project *core.Project) error {
		if _, ok := project.SharedResultConfigs[id]; !ok {
			return fmt.Errorf("%w: configuration %q", core.ErrNotFound, id)
		}
		delete(project.SharedResultConfigs, id)
		return nil
	})
}
