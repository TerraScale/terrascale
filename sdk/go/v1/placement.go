package v1

import (
	"context"
	"errors"
	"net/http"
	"slices"
)

type DatabasePlacement struct {
	Regions    []string `json:"regions,omitzero"`
	AllRegions bool     `json:"allRegions,omitzero"`
}

func (c *Client) SetDatabasePlacement(ctx context.Context, project, database string, placement DatabasePlacement) (*ManagedDatabase, error) {
	if (placement.Regions != nil) == placement.AllRegions {
		return nil, errors.New("terrabase: set exactly one of Regions or AllRegions")
	}
	if err := checkPlacementRegions(placement.Regions); err != nil {
		return nil, err
	}
	return databaseCall[ManagedDatabase](ctx, c.Database(project, database), http.MethodPatch, nil, placement)
}

func checkPlacementRegions(regions []string) error {
	if regions == nil {
		return nil
	}
	if len(regions) < 1 || len(regions) > 9 {
		return errors.New("terrabase: Regions must contain one to nine region ids")
	}
	for i, region := range regions {
		if region == "" || slices.Contains(regions[:i], region) {
			return errors.New("terrabase: Regions must contain distinct region ids")
		}
	}
	return nil
}
