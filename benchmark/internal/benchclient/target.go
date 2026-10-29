package benchclient

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

type Targets struct {
	Kind     string                  `json:"kind"`
	Project  string                  `json:"project"`
	Database string                  `json:"database"`
	Regions  map[string]RegionTarget `json:"regions"`

	PID      int               `json:"pid,omitempty"`
	DataDirs map[string]string `json:"data_dirs,omitempty"`

	Machines map[string]string `json:"machines,omitempty"`
}

type RegionTarget struct {
	URL   string `json:"url"`
	Token string `json:"token"`
	CAPEM string `json:"ca_pem,omitempty"`

	H2C bool `json:"h2c,omitempty"`
}

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

var ErrRegionMismatch = errors.New("served by another region")

type Region struct {
	Name     string
	base     *url.URL
	token    atomic.Pointer[string]
	client   *http.Client
	dbPath   string
	Mismatch atomic.Int64

	Pin string

	Header http.Header
}

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
	if rt.H2C {
		tr.Protocols = new(http.Protocols)
		tr.Protocols.SetUnencryptedHTTP2(true)
	}
	region := &Region{
		Name:   name,
		base:   base,
		client: &http.Client{Transport: tr, Timeout: 60 * time.Second},
		dbPath: "/v1/projects/" + t.Project + "/databases/" + t.Database,
	}
	region.SetToken(rt.Token)
	return region, nil
}

type Response struct {
	Status        int
	Body          []byte
	RetryAfter    time.Duration
	HasRetryAfter bool
}

func (r *Region) SetToken(token string) { r.token.Store(&token) }

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
	if token := *r.token.Load(); token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	for name, values := range r.Header {
		req.Header[name] = values
	}
	if r.Pin != "" {
		req.Header.Set("X-TerraScale-Region-Pin", r.Pin)
	}
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
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
	out := Response{Status: resp.StatusCode, Body: b, HasRetryAfter: resp.Header.Get("Retry-After") != ""}
	if s, err := strconv.Atoi(resp.Header.Get("Retry-After")); err == nil {
		out.RetryAfter = time.Duration(s) * time.Second
	}
	return out, nil
}

func DocPath(collection, id string) string {
	return "/collections/" + collection + "/documents/by-key?id=" + url.QueryEscape(id)
}
