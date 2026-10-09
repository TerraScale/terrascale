package tsgo

import (
	"context"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/sdk"
)

// StatusRegions reads TS-752's public measurements through ts-go. It never
// receives or forwards the user's ts-auth token to the status service.
type StatusRegions struct{ client *sdk.StatusClient }

func NewStatusRegions(baseURL string) (StatusRegions, error) {
	client, err := sdk.NewClient(sdk.Config{StatusBaseURL: baseURL, RequestTimeout: 5 * time.Second})
	if err != nil {
		return StatusRegions{}, err
	}
	return StatusRegions{client: client.Status}, nil
}

func (r StatusRegions) RegionHealth(ctx context.Context, codes []string) ([]core.RegionHealth, error) {
	response, err := r.client.Latency(ctx, "")
	if err != nil {
		return nil, err
	}
	wanted := make(map[string]bool, len(codes))
	for _, code := range codes {
		wanted[strings.ToUpper(code)] = true
	}
	var rows []core.RegionHealth
	for _, region := range response.Regions {
		code := strings.ToUpper(region.Code)
		if !wanted[code] {
			continue
		}
		row := core.RegionHealth{Region: code, Status: region.Status}
		if region.Consensus != nil && region.Consensus.MedianMS != nil && region.Consensus.Samples > 0 {
			row.LatencyMS = new(float64(*region.Consensus.MedianMS))
		}
		rows = append(rows, row)
	}
	return rows, nil
}
