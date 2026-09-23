package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

const publicAuthBaseURL = "https://auth.terrascale.tech"

// tsAuthEndpointDiscovery caches Atlas' ranked auth origins without sharing
// credentials with the metadata request.
type tsAuthEndpointDiscovery struct {
	mu      sync.Mutex
	bases   []string
	expires time.Time
}

// Auth requests never follow redirects, so a regional response cannot move
// a management credential or discovery request to another origin.
func tsAuthNoRedirectClient(client *http.Client) *http.Client {
	copy := *client
	copy.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	return &copy
}

func (d *tsAuthEndpointDiscovery) resolve(ctx context.Context, client *http.Client, atlasBase string) ([]string, error) {
	d.mu.Lock()
	if time.Now().Before(d.expires) {
		bases := d.bases
		d.mu.Unlock()
		return bases, nil
	}
	d.mu.Unlock()

	if atlasBase == "" {
		atlasBase = DefaultDiscoveryBaseURL
	}
	discoveryCtx, cancel := context.WithTimeout(ctx, discoveryTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(discoveryCtx, http.MethodGet, strings.TrimSuffix(atlasBase, "/")+"/api/v1/auth/discovery", nil)
	if err != nil {
		return nil, err
	}
	resp, err := tsAuthNoRedirectClient(client).Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("Atlas returned HTTP %d", resp.StatusCode)
	}
	const maxBody = 65_536
	if resp.ContentLength > maxBody {
		return nil, errors.New("Atlas auth discovery response is too large")
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, maxBody+1))
	if err != nil {
		return nil, err
	}
	if len(data) > maxBody {
		return nil, errors.New("Atlas auth discovery response is too large")
	}
	var doc struct {
		Version        int `json:"v"`
		RefreshSeconds int `json:"refreshSeconds"`
		Endpoints      []struct {
			Region string `json:"region"`
			URL    string `json:"url"`
		} `json:"endpoints"`
	}
	if err := json.Unmarshal(data, &doc); err != nil || doc.Version != 1 || doc.RefreshSeconds < 1 || doc.RefreshSeconds > 3600 || len(doc.Endpoints) < 1 || len(doc.Endpoints) > 20 {
		return nil, errors.New("Atlas auth discovery response is invalid")
	}
	bases := make([]string, 0, len(doc.Endpoints))
	seen := make(map[string]bool, len(doc.Endpoints))
	for _, endpoint := range doc.Endpoints {
		if !IsRegionCode(endpoint.Region) || seen[endpoint.Region] {
			return nil, errors.New("Atlas auth endpoint has an invalid region")
		}
		u, err := url.Parse(endpoint.URL)
		if err != nil || u.Scheme != "https" || u.Hostname() != strings.ToLower(endpoint.Region)+".auth.terrascale.tech" ||
			u.Port() != "" || u.User != nil || (u.Path != "" && u.Path != "/") || u.RawQuery != "" || u.Fragment != "" {
			return nil, errors.New("Atlas auth endpoint URL does not match its region")
		}
		seen[endpoint.Region] = true
		bases = append(bases, u.Scheme+"://"+u.Host)
	}
	d.mu.Lock()
	d.bases = bases
	d.expires = time.Now().Add(time.Duration(min(doc.RefreshSeconds, 60)) * time.Second)
	d.mu.Unlock()
	return bases, nil
}
