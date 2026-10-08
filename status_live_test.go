package sdk

import (
	"errors"
	"net/http"
	"net/url"
	"os"
	"slices"
	"strconv"
	"strings"
	"testing"
)

// The strict lane is the merge gate once the ts-status latency PR is deployed.
func TestStatusLatencyLive(t *testing.T) {
	require := os.Getenv("TERRASCALE_STATUS_REQUIRE_LATENCY") == "1"
	if !require && os.Getenv("TERRASCALE_STATUS_LIVE") != "1" {
		t.Skip("set TERRASCALE_STATUS_LIVE=1 to probe the public endpoint, or TERRASCALE_STATUS_REQUIRE_LATENCY=1 to require it")
	}
	var headers http.Header
	client, err := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		resp, err := http.DefaultTransport.RoundTrip(r)
		if err == nil {
			headers = resp.Header.Clone()
		}
		return resp, err
	})}})
	if err != nil {
		t.Fatal(err)
	}
	out, err := client.Status.Latency(t.Context(), "")
	var apiErr *APIError
	if !require && errors.As(err, &apiErr) && apiErr.Status == http.StatusNotFound {
		t.Skip("ts-status has not deployed /api/latency yet")
	}
	if err != nil {
		t.Fatal(err)
	}
	if headers.Get("Access-Control-Allow-Origin") != "*" || !strings.Contains(headers.Get("Content-Type"), "application/json") {
		t.Fatal("latency endpoint must return public CORS JSON")
	}
	cache := headers.Get("Cache-Control")
	cached := false
	for part := range strings.SplitSeq(cache, ",") {
		key, value, _ := strings.Cut(strings.TrimSpace(part), "=")
		if key == "max-age" || key == "s-maxage" {
			seconds, err := strconv.Atoi(value)
			cached = cached || (err == nil && seconds > 0)
		}
	}
	if !cached {
		t.Fatal("latency endpoint must specify a positive cache lifetime")
	}
	if out.GeneratedAt.IsZero() || out.Window != "1h" || len(out.Regions) == 0 {
		t.Fatal("latency response must contain generated_at, the 1h window, and regions")
	}
	for _, region := range out.Regions {
		ping, err := url.Parse(region.PingURL)
		if region.Code == "" || region.Code != strings.ToLower(region.Code) || region.Name == "" || err != nil || ping.Scheme != "https" || ping.Host == "" ||
			!slices.Contains([]string{"operational", "degraded", "down", "planned"}, region.Status) || region.Vantages == nil {
			t.Fatalf("region %q does not match the latency contract", region.Code)
		}
		checkStats := func(stats LatencyStats) {
			t.Helper()
			if stats.Samples < 0 || (stats.Samples == 0 && (stats.MedianMS != nil || stats.P95MS != nil)) ||
				(stats.Samples > 0 && (stats.MedianMS == nil || stats.P95MS == nil || *stats.MedianMS < 0 || *stats.P95MS < *stats.MedianMS)) {
				t.Fatalf("region %q has invalid latency statistics", region.Code)
			}
		}
		if region.Consensus != nil {
			checkStats(*region.Consensus)
		}
		for _, vantage := range region.Vantages {
			if vantage.Code == "" || vantage.Name == "" {
				t.Fatalf("region %q has an unnamed vantage", region.Code)
			}
			checkStats(vantage.LatencyStats)
		}
	}
	code := out.Regions[0].Code
	filtered, err := client.Status.Latency(t.Context(), code)
	if err != nil {
		t.Fatal(err)
	}
	if filtered.GeneratedAt.IsZero() || filtered.Window != "1h" || len(filtered.Regions) != 1 || filtered.Regions[0].Code != code {
		t.Fatal("region query must filter the response to the requested region")
	}
}
