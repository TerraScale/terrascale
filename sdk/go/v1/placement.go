package v1

import (
	"context"
	"errors"
	"net/http"
)

type DatabasePlacement struct {
	Regions    []string `json:"regions,omitzero"`
	AllRegions bool     `json:"allRegions,omitzero"`
}

func (c *Client) SetDatabasePlacement(ctx context.Context, project, database string, placement DatabasePlacement) (*ManagedDatabase, error) {
	if (placement.Regions != nil) == placement.AllRegions {
		return nil, errors.New("terrabase: set exactly one of Regions or AllRegions")
	}
	return databaseCall[ManagedDatabase](ctx, c.Database(project, database), http.MethodPatch, nil, placement)
}
