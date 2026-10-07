package terrabase

import (
	"context"
	"encoding/json/v2"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// Meter reads usage from TerraBase's Management API (GET …/usage on the
// organization, project and database routes) with the signed-in user's
// ts-auth access token.
// ponytail: a dashboard-side client, against "services talk only through the
// SDKs", until ts-go's Management client (which acts as ts-auth users since
// ts-go#19) gains the usage routes of terrabase#42 and the dashboard moves to
// that ts-go; then call it and delete this type.
type Meter struct {
	BaseURL string
	Client  *http.Client
}

var _ core.UsageMeter = Meter{}

// meterTimeout bounds one usage request, so a slow TerraBase delays a page
// by at most this long.
const meterTimeout = 10 * time.Second

func (m Meter) Usage(ctx context.Context, token string, scope core.UsageScope) (core.Usage, error) {
	path := []string{"organizations", scope.OrganizationID}
	if scope.ProjectID != "" {
		path = append(path, "projects", scope.ProjectID)
		if scope.DatabaseID != "" {
			path = append(path, "databases", scope.DatabaseID)
		}
	}
	endpoint := strings.TrimRight(m.BaseURL, "/") + "/v1/management"
	for _, segment := range path {
		if strings.TrimSpace(segment) == "" {
			return core.Usage{}, core.Invalid("usage scope needs an organization, and a project for a database")
		}
		endpoint += "/" + url.PathEscape(segment)
	}
	query := url.Values{"granularity": {scope.Granularity}, "from": {scope.From.UTC().Format(time.RFC3339)}}
	ctx, cancel := context.WithTimeout(ctx, meterTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint+"/usage?"+query.Encode(), nil)
	if err != nil {
		return core.Usage{}, err
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/json")
	client := m.Client
	if client == nil {
		client = http.DefaultClient
	}
	res, err := client.Do(req)
	if err != nil {
		return core.Usage{}, fmt.Errorf("%w: usage: %w", core.ErrUnavailable, err)
	}
	defer res.Body.Close()
	switch res.StatusCode {
	case http.StatusOK:
	case http.StatusNotFound:
		// TerraBase answers 404 for resources it does not store and for
		// organizations the user is not a member of there.
		return core.Usage{}, core.ErrNotMetered
	case http.StatusUnauthorized:
		return core.Usage{}, fmt.Errorf("%w: usage: TerraBase refused the access token", core.ErrUnauthorized)
	default:
		return core.Usage{}, fmt.Errorf("%w: usage: TerraBase answered %s", core.ErrUnavailable, res.Status)
	}
	// A 30-day hourly series is under 100 KiB; 4 MiB bounds a broken answer.
	body, err := io.ReadAll(io.LimitReader(res.Body, 4<<20))
	if err != nil {
		return core.Usage{}, fmt.Errorf("%w: usage: %w", core.ErrUnavailable, err)
	}
	var usage core.Usage
	if err := json.Unmarshal(body, &usage); err != nil {
		return core.Usage{}, fmt.Errorf("%w: usage: %w", core.ErrUnavailable, err)
	}
	if usage.Reads < 0 || usage.Writes < 0 || usage.Period.Reads < 0 || usage.Period.Writes < 0 {
		return core.Usage{}, fmt.Errorf("%w: usage: negative counts", core.ErrUnavailable)
	}
	return usage, nil
}
