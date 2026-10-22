package pitrbench

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Client talks to a local node's authenticated internal listener over HTTP/2 without TLS.
type Client struct {
	base   string
	owner  string
	secret string
	hc     *http.Client
}

// NewClient builds a client of the internal listener on port. Every request carries the node's peer credential
// (X-TerraBase-Peer-Credential): without it the internal listener answers 401.
func NewClient(port int, peerSecret, owner string, timeout time.Duration) *Client {
	var p http.Protocols
	p.SetUnencryptedHTTP2(true)
	return &Client{
		base:   "http://127.0.0.1:" + strconv.Itoa(port),
		owner:  owner,
		secret: peerSecret,
		hc: &http.Client{Transport: &http.Transport{Protocols: &p, IdleConnTimeout: 90 * time.Second}, Timeout: timeout,
			CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }},
	}
}

// Response is a finished call.
type Response struct {
	Status     int
	Body       []byte
	ETag       string
	RetryAfter time.Duration
	Date       time.Time // the node's Date header (1 s resolution), zero when absent
}

// Do sends one request; path is absolute ("/v1/...").
func (c *Client) Do(ctx context.Context, method, path string, body []byte) (Response, error) {
	if _, err := hex.DecodeString(c.secret); len(c.secret) != 64 || err != nil {
		return Response{}, fmt.Errorf("TERRABASE_PEER_SECRET must contain 64 hexadecimal characters")
	}
	var rd io.Reader
	if body != nil {
		rd = bytes.NewReader(body)
	}
	req, err := http.NewRequestWithContext(ctx, method, c.base+path, rd)
	if err != nil {
		return Response{}, err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	// The owner header only on the Management API: on a project's data route it would make the operator act as that
	// user, who holds no scopes there, instead of holding every scope (src/TerraBase/Features/UserAuth/OperatorHandler.cs).
	req.Header.Set("X-TerraBase-Peer-Credential", c.secret)
	if c.owner != "" && strings.HasPrefix(path, "/v1/management/") {
		req.Header.Set("X-TerraBase-User-Id", c.owner)
	}
	resp, err := c.hc.Do(req)
	if err != nil {
		return Response{}, err
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(resp.Body)
	if err != nil {
		return Response{}, err
	}
	out := Response{Status: resp.StatusCode, Body: b, ETag: strings.Trim(resp.Header.Get("ETag"), `"`)}
	if s, err := strconv.Atoi(resp.Header.Get("Retry-After")); err == nil {
		out.RetryAfter = time.Duration(s) * time.Second
	}
	out.Date, _ = http.ParseTime(resp.Header.Get("Date"))
	return out, nil
}

// JSON sends a JSON body and decodes a 2xx answer into out (when non-nil).
func (c *Client) JSON(ctx context.Context, method, path string, in, out any) (Response, error) {
	var body []byte
	if in != nil {
		var err error
		if body, err = json.Marshal(in); err != nil {
			return Response{}, err
		}
	}
	resp, err := c.Do(ctx, method, path, body)
	if err != nil {
		return resp, err
	}
	if resp.Status/100 != 2 {
		return resp, fmt.Errorf("%s %s: HTTP %d %s", method, path, resp.Status, truncate(resp.Body, 400))
	}
	if out != nil && len(resp.Body) > 0 {
		if err := json.Unmarshal(resp.Body, out); err != nil {
			return resp, fmt.Errorf("%s %s: %w", method, path, err)
		}
	}
	return resp, nil
}

func truncate(b []byte, n int) string {
	if len(b) > n {
		return string(b[:n]) + "…"
	}
	return string(b)
}

// NodeClock bounds the node's clock minus this host's (ms) from one answer's Date header: at some instant between
// sending and receiving, the node's time was in [Date, Date+2 s) (whole seconds, and Kestrel refreshes the header once
// a second). Rollback marks a Date earlier than the previous one.
type NodeClock struct {
	LoMs     int64 `json:"lo_ms"`
	HiMs     int64 `json:"hi_ms"`
	Rollback bool  `json:"rollback,omitempty"`
}

// Clock measures NodeClock with one GET /replication; prev is the previous Date (zero: none).
func (c *Client) Clock(ctx context.Context, prev time.Time) (*NodeClock, time.Time, error) {
	sent := time.Now()
	resp, err := c.Do(ctx, http.MethodGet, "/replication", nil)
	recv := time.Now()
	if err != nil || resp.Date.IsZero() {
		return nil, prev, fmt.Errorf("node clock: no Date header (%v)", err)
	}
	d := resp.Date.UnixMilli()
	return &NodeClock{LoMs: d - recv.UnixMilli(), HiMs: d + 1999 - sent.UnixMilli(), Rollback: resp.Date.Before(prev)}, resp.Date, nil
}

// DatabasePath is the project route of a database by name.
func (r *Run) DatabasePath(database string) string {
	return "/v1/projects/" + r.Project + "/databases/" + database
}

// ManagementPath is the run's database under the Management API alias, by id: on the internal listener, project
// management routes (indexes, point-in-time) answer only there, as the user the owner header names
// (src/TerraBase/Hosting/Listeners.cs, Features/Management/LegacyRoutes.cs).
func (r *Run) ManagementPath() string {
	return "/v1/management/organizations/" + r.Organization + "/projects/" + r.Project + "/databases/" + r.DatabaseID
}

// BackupFile is one file of GET /v1/backup (the node's admin status).
type BackupFile struct {
	File          string     `json:"file"`
	Generation    *string    `json:"generation"`
	LogBytes      int64      `json:"logBytes"`
	LastShippedAt *time.Time `json:"lastShippedAt"`
	LagSeconds    float64    `json:"lagSeconds"`
	PendingBytes  int64      `json:"pendingBytes"`
	LastError     *string    `json:"lastError"`
	Shipping      bool       `json:"shipping"`
	SizeBytes     *int64     `json:"sizeBytes"`
}

// BackupStatus reads GET /v1/backup and returns the file whose name holds databaseID.
func (c *Client) BackupStatus(ctx context.Context, databaseID string) (BackupFile, bool, error) {
	var status struct {
		Shipping bool         `json:"shipping"`
		Files    []BackupFile `json:"files"`
	}
	if _, err := c.JSON(ctx, http.MethodGet, "/v1/backup", nil, &status); err != nil {
		return BackupFile{}, false, err
	}
	for _, f := range status.Files {
		if strings.Contains(f.File, databaseID) {
			return f, true, nil
		}
	}
	return BackupFile{}, false, nil
}
