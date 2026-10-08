// Package atlas publishes signed documents to ts-atlas, TerraScale's region
// discovery (ts-atlas docs/discovery-v1.md, Publishing): the topology of
// regions and services, and project placements.
//
//	p := atlas.Publisher{BaseURL: "https://atlas.terrascale.tech", Key: key}
//	report, err := p.Publish(ctx, atlas.Document{Topology: &topology})
package atlas

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

// Document is one publish: the whole topology, placements, or both.
type Document struct {
	Topology   *Topology   `json:"topology,omitempty"`
	Placements []Placement `json:"placements,omitempty"`
}

// Topology lists every region (1-64) and service (0-32). Publish it whole,
// bumping Updated (unix seconds) for every change: Atlas keeps the newest.
// The services must include terrabase, auth, dashboard, status and docs.
type Topology struct {
	V        int       `json:"v"`
	Updated  int64     `json:"updated"`
	Regions  []Region  `json:"regions"`
	Services []Service `json:"services"`
}

// Region is a region with its bare API and auth origins and its health, the
// region's GET <API>/readyz result.
type Region struct {
	ID      string  `json:"id"`
	Name    string  `json:"name"`
	Country string  `json:"country"`
	Lat     float64 `json:"lat"`
	Lon     float64 `json:"lon"`
	API     string  `json:"api"`
	Auth    string  `json:"auth"`
	OK      bool    `json:"ok"`
}

// Service is an entry of the service catalog.
type Service struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	URL    string `json:"url"`
	Health string `json:"health,omitempty"`
}

// Placement is where a project and its databases live, monotonic on Updated.
type Placement struct {
	V         int               `json:"v"`
	Project   string            `json:"project"`
	Region    string            `json:"region"`
	Databases map[string]string `json:"databases,omitempty"`
	Updated   int64             `json:"updated"`
}

// Report is Atlas' answer: "updated", "stale" (an older or conflicting
// Updated, not written) or "skipped" for the topology, and one result per
// placement in input order.
type Report struct {
	Topology   string `json:"topology"`
	Placements []struct {
		Project string `json:"project"`
		Result  string `json:"result"`
	} `json:"placements"`
}

// Error is an Atlas error response, such as 401 invalid_signature (a key
// not in PUBLISH_KEYS, or a clock off by more than 300 s), 400 invalid_*,
// or 502 store_unavailable, which is safe to retry: publishes are monotonic.
type Error struct {
	Status  int
	Code    string `json:"code"`
	Message string `json:"message"`
	Fix     string `json:"fix"`
}

func (e *Error) Error() string {
	return fmt.Sprintf("atlas: %d %s: %s", e.Status, e.Code, e.Message)
}

// Publisher signs and posts documents. It is safe for concurrent use.
type Publisher struct {
	// BaseURL is the Atlas origin, such as https://atlas.terrascale.tech,
	// without a path.
	BaseURL string
	// Key is a P-256 key whose public JWK is in Atlas' PUBLISH_KEYS.
	Key *ecdsa.PrivateKey
	// HTTPClient nil uses a client with a 30 s timeout. It is copied and
	// never follows redirects.
	HTTPClient *http.Client
}

// Publish signs doc with the current time and posts it.
func (p *Publisher) Publish(ctx context.Context, doc Document) (*Report, error) {
	for _, placement := range doc.Placements {
		if err := entityid.Check(placement.Project); err != nil {
			return nil, err
		}
	}
	base, err := url.Parse(p.BaseURL)
	if err != nil || base.Host == "" || base.User != nil || base.RawQuery != "" || base.ForceQuery || base.Fragment != "" ||
		(base.Path != "" && base.Path != "/") || (base.Scheme != "https" && !(base.Scheme == "http" && loopback(base.Hostname()))) {
		return nil, fmt.Errorf("atlas: BaseURL %q must be an https origin without a path", p.BaseURL)
	}
	endpoint := *base
	endpoint.Path, endpoint.RawPath = "/v1/publish", ""
	body, err := json.Marshal(doc)
	if err != nil {
		return nil, fmt.Errorf("atlas: encode document: %w", err)
	}
	timestamp := strconv.FormatInt(time.Now().Unix(), 10)
	signature, err := Sign(p.Key, timestamp, body)
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint.String(), bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-Atlas-Timestamp", timestamp)
	req.Header.Set("X-Atlas-Signature", signature)
	hc := http.Client{Timeout: 30 * time.Second}
	if p.HTTPClient != nil {
		hc = *p.HTTPClient
	}
	// A redirect would carry the signed body and headers to another origin.
	hc.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	res, err := hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("atlas: publish: %w", err)
	}
	defer res.Body.Close()
	answer, err := io.ReadAll(io.LimitReader(res.Body, 64<<10))
	if err != nil {
		return nil, fmt.Errorf("atlas: read publish response: %w", err)
	}
	if res.StatusCode != http.StatusOK {
		e := &Error{Status: res.StatusCode}
		var env struct{ Error *Error }
		if json.Unmarshal(answer, &env) == nil && env.Error != nil {
			e.Code, e.Message, e.Fix = env.Error.Code, env.Error.Message, env.Error.Fix
		}
		return nil, e
	}
	var report Report
	if err := json.Unmarshal(answer, &report); err != nil {
		return nil, fmt.Errorf("atlas: decode publish response: %w", err)
	}
	for _, placement := range report.Placements {
		if err := entityid.Check(placement.Project); err != nil {
			return nil, err
		}
	}
	return &report, nil
}

// Sign is X-Atlas-Signature: base64url of the raw r||s ES256 signature over
// "atlas-publish-v1\n<timestamp>\n<body>".
func Sign(key *ecdsa.PrivateKey, timestamp string, body []byte) (string, error) {
	if key == nil || key.Curve != elliptic.P256() {
		return "", errors.New("atlas: the publish key must be a P-256 key")
	}
	digest := sha256.Sum256(slices.Concat([]byte("atlas-publish-v1\n"+timestamp+"\n"), body))
	r, s, err := ecdsa.Sign(rand.Reader, key, digest[:])
	if err != nil {
		return "", fmt.Errorf("atlas: sign: %w", err)
	}
	raw := make([]byte, 64)
	r.FillBytes(raw[:32])
	s.FillBytes(raw[32:])
	return base64.RawURLEncoding.EncodeToString(raw), nil
}

func loopback(host string) bool {
	ip := net.ParseIP(host)
	return host == "localhost" || ip != nil && ip.IsLoopback()
}
