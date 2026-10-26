package sdk

import (
	"context"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// DefaultStatusBaseURL is the public ts-status origin.
const DefaultStatusBaseURL = "https://status.terrascale.tech"

// StatusClient reads public ts-status measurements. Construct it with NewClient.
type StatusClient struct{ c *Client }

// LatencyResponse contains region latency aggregated over Window.
type LatencyResponse struct {
	GeneratedAt time.Time       `json:"generated_at"`
	Window      string          `json:"window"`
	Regions     []RegionLatency `json:"regions"`
}

// LatencyStats contains integer millisecond measurements. Nil measurements
// mean no samples; a measured zero is distinct from a missing measurement.
type LatencyStats struct {
	MedianMS *int `json:"median_ms"`
	P95MS    *int `json:"p95_ms"`
	Samples  int  `json:"samples"`
}

// RegionLatency is one region's consensus and individual vantage measurements.
type RegionLatency struct {
	Code string `json:"code"`
	Name string `json:"name"`
	// PingURL is nil when the region has no public browser ping target.
	PingURL   *string          `json:"ping_url"`
	Status    string           `json:"status"` // operational, degraded, down, or planned
	Consensus *LatencyStats    `json:"consensus"`
	Vantages  []VantageLatency `json:"vantages"`
}

// VantageLatency is latency measured from one probe location.
type VantageLatency struct {
	Code string `json:"code"`
	Name string `json:"name"`
	LatencyStats
}

// Latency reads GET /api/latency without credentials. An empty region requests
// all regions; otherwise the lowercase region code is sent as a query filter.
// It uses Config's HTTP client, request timeout, and retry policy.
func (s *StatusClient) Latency(ctx context.Context, region string) (*LatencyResponse, error) {
	query := url.Values{}
	if region = strings.ToLower(strings.TrimSpace(region)); region != "" {
		query.Set("region", region)
	}
	var out LatencyResponse
	if err := s.c.sendAuthorized(ctx, s.c.cfg.StatusBaseURL, http.MethodGet, "/api/latency", query, nil, "", tsAuthAuthorization{}, &out, "status.latency"); err != nil {
		return nil, err
	}
	return &out, nil
}

func statusResolveBase(raw string) (string, error) {
	base := strings.TrimSpace(raw)
	if base == "" {
		return DefaultStatusBaseURL, nil
	}
	return resolveServiceBase(base, "status_base_url", false)
}
