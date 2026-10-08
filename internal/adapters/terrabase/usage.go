package terrabase

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/sdk/v1"
)

// Meter reads TerraBase's project and database usage through ts-go, with the
// signed-in user's ts-auth access token.
type Meter struct {
	BaseURL string
	Client  *http.Client
}

var _ core.UsageMeter = Meter{}

const meterTimeout = 10 * time.Second

func (m Meter) Usage(ctx context.Context, token string, scope core.UsageScope) (core.Usage, error) {
	if strings.TrimSpace(scope.OrganizationID) == "" || strings.TrimSpace(scope.ProjectID) == "" {
		return core.Usage{}, core.Invalid("usage needs an organization and a project")
	}
	management, err := v1.NewManagement(v1.Config{BaseURL: m.BaseURL, HTTPClient: cmp.Or(m.Client, http.DefaultClient), MaxResponseBytes: 4 << 20})
	if err != nil {
		return core.Usage{}, fmt.Errorf("%w: usage: %w", core.ErrUnavailable, err)
	}
	ctx, cancel := context.WithTimeout(ctx, meterTimeout)
	defer cancel()
	usage, err := management.WithToken(token).Usage(ctx, scope.OrganizationID, scope.ProjectID, scope.DatabaseID, v1.UsageOptions{
		Granularity: scope.Granularity, From: scope.From,
	})
	if err != nil {
		var apiError *v1.APIError
		if errors.As(err, &apiError) {
			switch apiError.Status {
			case http.StatusNotFound:
				return core.Usage{}, core.ErrNotMetered
			case http.StatusUnauthorized:
				return core.Usage{}, fmt.Errorf("%w: usage: TerraBase refused the access token", core.ErrUnauthorized)
			}
		}
		return core.Usage{}, fmt.Errorf("%w: usage: %w", core.ErrUnavailable, err)
	}
	return meteredUsage(*usage)
}

func meteredUsage(usage v1.Usage) (core.Usage, error) {
	if usage.Period == nil {
		return core.Usage{}, core.ErrNotMetered
	}
	if usage.StorageBytes < 0 || usage.LogicalBytes < 0 || usage.Documents < 0 || usage.Reads < 0 || usage.Writes < 0 || usage.Period.Reads < 0 || usage.Period.Writes < 0 {
		return core.Usage{}, fmt.Errorf("%w: usage: negative counts", core.ErrUnavailable)
	}
	result := core.Usage{
		ProjectID: usage.ProjectID, DatabaseID: usage.DatabaseID,
		StorageBytes: usage.StorageBytes, LogicalBytes: usage.LogicalBytes, Documents: usage.Documents, Reads: usage.Reads, Writes: usage.Writes,
		Period: core.UsagePeriod{From: usage.Period.From, To: usage.Period.To, Granularity: usage.Period.Granularity, Reads: usage.Period.Reads, Writes: usage.Period.Writes},
		Series: make([]core.UsagePoint, 0, len(usage.Series)), Items: make([]core.Usage, 0, len(usage.Items)),
	}
	for _, point := range usage.Series {
		if point.Reads < 0 || point.Writes < 0 {
			return core.Usage{}, fmt.Errorf("%w: usage: negative counts", core.ErrUnavailable)
		}
		result.Series = append(result.Series, core.UsagePoint{Start: point.Start, Reads: point.Reads, Writes: point.Writes})
	}
	for _, item := range usage.Items {
		converted, err := meteredUsage(item)
		if err != nil {
			return core.Usage{}, err
		}
		result.Items = append(result.Items, converted)
	}
	return result, nil
}
