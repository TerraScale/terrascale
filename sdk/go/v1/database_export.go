package v1

import (
	"context"
	"fmt"
	"io"
	"net/http"
)

// ExportDatabase streams a database's SQL dump with sql:read; the caller closes the returned body.
func (c *Client) ExportDatabase(ctx context.Context, project, database string) (io.ReadCloser, error) {
	d := c.Database(project, database)
	p, err := d.path("export")
	if err != nil {
		return nil, err
	}
	pc, err := c.projectClient(d.project)
	if err != nil {
		return nil, err
	}
	target, err := pc.url(ctx, p, nil)
	if err != nil {
		return nil, err
	}
	token, err := pc.bearerToken(ctx, "")
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "text/plain")
	req.Header.Set("Authorization", "Bearer "+token)
	pc.pin(req.Header)
	res, err := pc.hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("terrabase: database export request: %w", err)
	}
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		defer res.Body.Close()
		data, readErr := io.ReadAll(io.LimitReader(res.Body, 64<<10))
		api := parseAPIError(res.StatusCode, res.Header, data)
		api.cause = readErr
		return nil, api
	}
	return res.Body, nil
}
