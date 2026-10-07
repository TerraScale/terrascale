// Package atlas reads the region catalog from ts-atlas discovery
// (GET /v1/discovery, ts-atlas docs/discovery-v1.md). TerraBase's regions
// are published there; the dashboard never hard-codes them.
package atlas

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strings"
	"sync"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// ttl is how long a fetched catalog is served before discovery is read again.
const ttl = time.Minute

// Catalog caches discovery's regions. It is safe for concurrent use.
type Catalog struct {
	// BaseURL is the Atlas origin, such as https://atlas.terrascale.tech.
	BaseURL string
	// Client nil uses a client with a 5 s timeout.
	Client *http.Client

	mu      sync.Mutex
	regions []core.Region
	fetched time.Time
}

// Regions returns discovery's regions in publication order. When Atlas is
// unreachable it serves the last catalog it read, however old.
func (c *Catalog) Regions(ctx context.Context) ([]core.Region, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.regions != nil && time.Since(c.fetched) < ttl {
		return slices.Clone(c.regions), nil
	}
	regions, err := c.fetch(ctx)
	if err != nil {
		if c.regions != nil {
			return slices.Clone(c.regions), nil
		}
		return nil, fmt.Errorf("%w: region discovery: %v", core.ErrDataPlane, err)
	}
	c.regions, c.fetched = regions, time.Now()
	return slices.Clone(regions), nil
}

func (c *Catalog) fetch(ctx context.Context) ([]core.Region, error) {
	client := c.Client
	if client == nil {
		client = &http.Client{Timeout: 5 * time.Second}
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.BaseURL, "/")+"/v1/discovery", nil)
	if err != nil {
		return nil, err
	}
	res, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("GET /v1/discovery: %s", res.Status)
	}
	var doc struct {
		Regions []struct {
			ID      string `json:"id"`
			Name    string `json:"name"`
			Country string `json:"country"`
			API     string `json:"api"`
			Health  string `json:"health"`
		} `json:"regions"`
	}
	if err := json.NewDecoder(io.LimitReader(res.Body, 1<<20)).Decode(&doc); err != nil {
		return nil, err
	}
	// Discovery ranks regions by distance from the caller; sort by code so
	// the picker's order does not depend on where the dashboard runs.
	regions := make([]core.Region, 0, len(doc.Regions))
	for _, r := range doc.Regions {
		code := Code(r.ID)
		// ponytail: one region per code; gru-1 and gru-2 would collapse to GRU
		// until the dashboard keys placement by Atlas region id (TS-663).
		if code == "" || slices.ContainsFunc(regions, func(have core.Region) bool { return have.Code == code }) {
			continue
		}
		regions = append(regions, core.Region{Code: code, ID: r.ID, Name: r.Name, Country: strings.ToLower(r.Country), API: r.API, Healthy: r.Health != "down"})
	}
	if len(regions) == 0 {
		return nil, errors.New("discovery lists no regions")
	}
	slices.SortFunc(regions, func(a, b core.Region) int { return strings.Compare(a.Code, b.Code) })
	return regions, nil
}

// Code maps supported Atlas region ids to GRU or SFO. Legacy ids use the hub prefix.
func Code(id string) string {
	switch id {
	case "sa-saopaulo-1":
		return "GRU"
	case "us-sanjose-1":
		return "SFO"
	}
	prefix, _, _ := strings.Cut(id, "-")
	switch prefix {
	case "gru", "sfo":
		return strings.ToUpper(prefix)
	}
	return ""
}
