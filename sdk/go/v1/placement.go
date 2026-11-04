package v1

import (
	"context"
	"errors"
	"net/http"
	"time"
)

const MoveHomeTimeout = 5 * time.Minute

const (
	ErrMoveInProgress  ErrorCode = "move_in_progress"
	ErrReplicaNotReady ErrorCode = "replica_not_ready"
	ErrMoveUnresolved  ErrorCode = "move_unresolved"
)

type DatabasePlacement struct {
	ReplicaRegions []string `json:"replicaRegions,omitzero"`
	AllRegions     bool     `json:"allRegions,omitzero"`
}

func (c *Client) SetDatabasePlacement(ctx context.Context, project, database string, placement DatabasePlacement) (*ManagedDatabase, error) {
	if (placement.ReplicaRegions != nil) == placement.AllRegions {
		return nil, errors.New("terrabase: set exactly one of ReplicaRegions or AllRegions")
	}
	return databaseCall[ManagedDatabase](ctx, c.Database(project, database), http.MethodPatch, nil, placement)
}

func (c *Client) MoveDatabaseHome(ctx context.Context, project, database, homeRegion string) (*ManagedDatabase, error) {
	if homeRegion == "" {
		return nil, errors.New("terrabase: homeRegion is required")
	}
	p, err := c.Database(project, database).path("move-home")
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, MoveHomeTimeout)
	defer cancel()
	body := struct {
		HomeRegion string `json:"homeRegion"`
	}{homeRegion}
	for attempt := 0; ; attempt++ {
		out, err := publicCall[ManagedDatabase](ctx, c, request{method: http.MethodPost, path: p, timeout: MoveHomeTimeout}, body)
		var apiErr *APIError
		if err == nil || attempt == 5 || !errors.As(err, &apiErr) || apiErr.Status != http.StatusServiceUnavailable || apiErr.Code != ErrMoveUnresolved {
			return out, err
		}
		delay := time.Second
		if apiErr.retryAfterSet {
			delay = apiErr.RetryAfter
		}
		if err := sleep(ctx, delay); err != nil {
			return nil, err
		}
	}
}
