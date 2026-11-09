package v1

import (
	"context"
	"fmt"
	"net/http"
)

// Database is a handle to one database of a project, by current name or immutable
// ULID. It performs no I/O.
type Database struct {
	c       *Client
	project string
	name    string
}

// Database returns a handle by current name or immutable ULID within a project.
// project is the project ID from the Management API; the API key's organization
// selects the tenant.
func (c *Client) Database(project, name string) *Database {
	return &Database{c: c, project: project, name: name}
}

// Project returns the project id.
func (d *Database) Project() string { return d.project }

// Name returns the database selector used to construct the handle.
func (d *Database) Name() string { return d.name }

func (d *Database) path(extra ...string) ([]string, error) {
	if err := checkID(d.project); err != nil {
		return nil, fmt.Errorf("terrabase: project ID: %w", err)
	}
	if err := checkDatabaseSelector(d.name); err != nil {
		return nil, err
	}
	return append([]string{"v1", "projects", d.project, "databases", d.name}, extra...), nil
}

// DatabaseInfo is a database as returned by Database.Create.
type DatabaseInfo struct {
	ID        string `json:"id" ulid:"required"`
	ProjectID string `json:"projectId" ulid:"required"`
	Name      string `json:"name"`
	Region    string `json:"region"`
	// Created is true when the call created the database (201).
	Created bool `json:"-"`
}

// Create creates the database in its project. An existing name fails with
// ErrNameTaken. Needs the databases:write scope.
func (d *Database) Create(ctx context.Context) (*DatabaseInfo, error) {
	if err := checkName("database", d.name); err != nil {
		return nil, err
	}
	p, err := d.path()
	if err != nil {
		return nil, err
	}
	var info DatabaseInfo
	resp, err := d.c.call(ctx, request{method: http.MethodPost, path: p[:len(p)-1]}, map[string]string{"name": d.name}, &info)
	if err != nil {
		return nil, err
	}
	info.Created = resp.status == http.StatusCreated
	return &info, nil
}

// Delete deletes the database and its data; its branches stay. Needs the
// databases:write scope.
func (d *Database) Delete(ctx context.Context) error {
	p, err := d.path()
	if err != nil {
		return err
	}
	_, err = d.c.call(ctx, request{method: http.MethodDelete, path: p}, nil, nil)
	return err
}
