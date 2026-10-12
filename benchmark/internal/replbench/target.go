// Package replbench drives the multi-region replication benchmark: a load of documents written across every region of
// one TerraBase database, replication lag probes, convergence and an exact correctness check of every region at the end.
package replbench

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"sync/atomic"
	"time"
)

// Targets names the cluster under test. The local cluster host writes it as JSON; a Fly run builds it from flags.
type Targets struct {
	Kind     string                  `json:"kind"` // "local" or "fly"
	Project  string                  `json:"project"`
	Database string                  `json:"database"`
	Regions  map[string]RegionTarget `json:"regions"`
	// Local only: the host process holding every node, and each node's data directory.
	PID      int               `json:"pid,omitempty"`
	DataDirs map[string]string `json:"data_dirs,omitempty"`
	// Fly only: the app, its org (for the metrics API) and each region's machine size.
	App      string            `json:"app,omitempty"`
	Org      string            `json:"org,omitempty"`
	Machines map[string]string `json:"machines,omitempty"`
}

// RegionTarget is one region's endpoint. Pin sends fly-prefer-region and refuses an answer served from elsewhere.
type RegionTarget struct {
	URL   string `json:"url"`
	Token string `json:"token"`
	CAPEM string `json:"ca_pem,omitempty"`
	Pin   bool   `json:"pin,omitempty"`
}

// LoadTargets reads a targets file and checks it names at least two regions with a URL and token each.
func LoadTargets(path string) (Targets, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return Targets{}, err
	}
	var t Targets
	if err := json.Unmarshal(b, &t); err != nil {
		return Targets{}, fmt.Errorf("%s: %w", path, err)
	}
	if t.Project == "" || t.Database == "" || len(t.Regions) == 0 {
		return Targets{}, fmt.Errorf("%s: needs project, database and at least one region", path)
	}
	for name, r := range t.Regions {
		// A Fly run's targets carry no key: the machines read it from the TS_BENCH_API_KEY Fly secret.
		if r.Token == "" {
			r.Token = os.Getenv("TS_BENCH_API_KEY")
			t.Regions[name] = r
		}
		if r.URL == "" || r.Token == "" {
			return Targets{}, fmt.Errorf("%s: region %s needs url and a token (in the file or TS_BENCH_API_KEY)", path, name)
		}
	}
	return t, nil
}

// ErrRegionMismatch is a pinned request Fly served from another region.
var ErrRegionMismatch = errors.New("served by another region")

// Region is one region's API client. Requests are never retried here; callers decide.
type Region struct {
	Name     string
	base     *url.URL
	token    string
	pin      bool
	client   *http.Client
	dbPath   string
	Mismatch atomic.Int64
}

// NewRegion builds the client of one region of t.
func NewRegion(name string, t Targets, conns int) (*Region, error) {
	rt := t.Regions[name]
	base, err := url.Parse(rt.URL)
	if err != nil {
		return nil, err
	}
	tr := &http.Transport{MaxIdleConns: conns, MaxIdleConnsPerHost: conns, MaxConnsPerHost: conns, IdleConnTimeout: 90 * time.Second, ForceAttemptHTTP2: true}
	if rt.CAPEM != "" {
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM([]byte(rt.CAPEM)) {
			return nil, fmt.Errorf("region %s: ca_pem holds no certificate", name)
		}
		tr.TLSClientConfig = &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}
	}
	return &Region{
		Name:   name,
		base:   base,
		token:  rt.Token,
		pin:    rt.Pin,
		client: &http.Client{Transport: tr, Timeout: 60 * time.Second},
		dbPath: "/v1/projects/" + t.Project + "/databases/" + t.Database,
	}, nil
}

// Response is a finished call: status, body and the server's Retry-After, if any.
type Response struct {
	Status     int
	Body       []byte
	RetryAfter time.Duration
}

// Do sends one request below the database path. path starts with "/".
func (r *Region) Do(ctx context.Context, method, path string, body []byte, contentType string) (Response, error) {
	u := *r.base
	rel, err := url.Parse(r.dbPath + path)
	if err != nil {
		return Response{}, err
	}
	u.Path, u.RawQuery = rel.Path, rel.RawQuery
	var rd io.Reader
	if body != nil {
		rd = bytes.NewReader(body)
	}
	req, err := http.NewRequestWithContext(ctx, method, u.String(), rd)
	if err != nil {
		return Response{}, err
	}
	req.Header.Set("Authorization", "Bearer "+r.token)
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	if r.pin {
		req.Header.Set("fly-prefer-region", r.Name)
	}
	resp, err := r.client.Do(req)
	if err != nil {
		return Response{}, err
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(resp.Body)
	if err != nil {
		return Response{}, err
	}
	if r.pin && resp.Header.Get("fly-region") != r.Name {
		r.Mismatch.Add(1)
		return Response{}, fmt.Errorf("%w: pinned %s, served %q", ErrRegionMismatch, r.Name, resp.Header.Get("fly-region"))
	}
	out := Response{Status: resp.StatusCode, Body: b}
	if s, err := strconv.Atoi(resp.Header.Get("Retry-After")); err == nil {
		out.RetryAfter = time.Duration(s) * time.Second
	}
	return out, nil
}

// DocPath is the by-key path of one document.
func DocPath(collection, id string) string {
	return "/collections/" + collection + "/documents/by-key?id=" + url.QueryEscape(id)
}
