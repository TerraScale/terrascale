package v1

import (
	"context"
	"fmt"
	"net/http"

	"terrascale.tech/sdk/internal/causal"
)

type Database struct {
	c       *Client
	project string
	name    string
	causal  *causal.Tracker
}

func (c *Client) Database(project, name string) *Database {
	tracker, _ := c.bookmarks.LoadOrStore(project+"/"+name, new(causal.Tracker))
	return &Database{c: c, project: project, name: name, causal: tracker.(*causal.Tracker)}
}

func (d *Database) Project() string { return d.project }

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

type DatabaseInfo struct {
	ID        string `json:"id" ulid:"required"`
	ProjectID string `json:"projectId" ulid:"required"`
	Name      string `json:"name"`
	Region    string `json:"region"`
	Created   bool   `json:"-"`
}

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

func (d *Database) Delete(ctx context.Context) error {
	p, err := d.path()
	if err != nil {
		return err
	}
	_, err = d.c.call(ctx, request{method: http.MethodDelete, path: p}, nil, nil)
	return err
}
