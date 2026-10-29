package apibench

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"fmt"
	"io"
	"maps"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"time"
)

type Stat struct {
	Min    float64 `json:"min"`
	Median float64 `json:"median"`
}

// NewStat returns the minimum and median of xs; the median averages the middle pair.
func NewStat(xs []float64) Stat {
	if len(xs) == 0 {
		return Stat{}
	}
	s := slices.Clone(xs)
	slices.Sort(s)
	m := len(s) / 2
	med := s[m]
	if len(s)%2 == 0 {
		med = (s[m-1] + s[m]) / 2
	}
	return Stat{Min: s[0], Median: med}
}

type Probe struct {
	TCPConnectMS   Stat `json:"tcp_connect_ms"`
	TLSHandshakeMS Stat `json:"tls_handshake_ms"`
	HealthzMS      Stat `json:"healthz_ms"`
}

// ProbeEndpoint times TCP connect, TLS handshake and GET /healthz n times. Each
// try uses a fresh connection so no try reuses another's.
func ProbeEndpoint(ctx context.Context, endpoint string, n int) (Probe, error) {
	return probeEndpoint(ctx, endpoint, n, nil)
}

func probeEndpoint(ctx context.Context, endpoint string, n int, roots *x509.CertPool) (Probe, error) {
	u, err := url.Parse(endpoint)
	if err != nil {
		return Probe{}, err
	}
	addr := u.Host
	if u.Port() == "" {
		addr = net.JoinHostPort(u.Hostname(), "443")
	}
	var tcp, tlsMS, healthz []float64
	for range n {
		t, h, z, err := probeOnce(ctx, endpoint, u.Hostname(), addr, roots)
		if err != nil {
			return Probe{}, err
		}
		tcp, tlsMS, healthz = append(tcp, t), append(tlsMS, h), append(healthz, z)
	}
	return Probe{TCPConnectMS: NewStat(tcp), TLSHandshakeMS: NewStat(tlsMS), HealthzMS: NewStat(healthz)}, nil
}

func probeOnce(ctx context.Context, endpoint, host, addr string, roots *x509.CertPool) (tcp, handshake, healthz float64, err error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint+"/healthz", nil)
	if err != nil {
		return 0, 0, 0, err
	}
	t := time.Now()
	var d net.Dialer
	conn, err := d.DialContext(ctx, "tcp", addr)
	if err != nil {
		return 0, 0, 0, err
	}
	tcp = msSince(t)
	t = time.Now()
	c := tls.Client(conn, &tls.Config{ServerName: host, RootCAs: roots, NextProtos: []string{"http/1.1"}})
	if err := c.HandshakeContext(ctx); err != nil {
		conn.Close()
		return 0, 0, 0, err
	}
	handshake = msSince(t)
	// From here the transport owns c and closes it once: on error, or in CloseIdleConnections.
	tr := &http.Transport{DialTLSContext: func(context.Context, string, string) (net.Conn, error) { return c, nil }}
	defer tr.CloseIdleConnections()
	t = time.Now()
	resp, err := (&http.Client{Transport: tr}).Do(req)
	if err != nil {
		return 0, 0, 0, err
	}
	defer resp.Body.Close()
	if _, err := io.Copy(io.Discard, resp.Body); err != nil {
		return 0, 0, 0, err
	}
	if resp.StatusCode != http.StatusOK {
		return 0, 0, 0, fmt.Errorf("healthz: HTTP %d", resp.StatusCode)
	}
	return tcp, handshake, msSince(t), nil
}

func msSince(t time.Time) float64 {
	return float64(time.Since(t)) / float64(time.Millisecond)
}

type StartupStat struct {
	Client   string `json:"client"`
	WallMS   Stat   `json:"wall_ms"`
	InitMS   Stat   `json:"init_ms"`
	MaxRSSMB Stat   `json:"max_rss_mb"`
}

type ClientRun struct {
	Client  string   `json:"client"`
	Ready   *Ready   `json:"ready,omitempty"`
	Results []Result `json:"results"`
	Done    *Done    `json:"done,omitempty"`
	Error   string   `json:"error,omitempty"`
}

type Run struct {
	ID           string        `json:"id"`
	Host         string        `json:"host"`
	ClusterState string        `json:"cluster_state"`
	Endpoint     string        `json:"endpoint"`
	Start        time.Time     `json:"start"`
	End          time.Time     `json:"end"`
	Probe        Probe         `json:"probe"`
	Startup      []StartupStat `json:"startup"`
	Clients      []ClientRun   `json:"clients"`
}

// WriteReport writes <dir>/<date>-<host>-<ID>.md and .json, refusing to overwrite.
// A failed write removes both files, so the same Run can be written again.
func WriteReport(dir string, r Run) (md, js string, err error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", "", err
	}
	base := filepath.Join(dir, fmt.Sprintf("%s-%s-%s", r.Start.UTC().Format("2006-01-02"), r.Host, r.ID))
	md, js = base+".md", base+".json"
	body, err := json.MarshalIndent(r, "", "  ")
	if err != nil {
		return "", "", err
	}
	mf, err := os.OpenFile(md, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
	if err != nil {
		return "", "", err
	}
	jf, err := os.OpenFile(js, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
	if err != nil {
		mf.Close()
		os.Remove(md)
		return "", "", err
	}
	// Uses base, not md/js: the error returns above clear the named results first.
	defer func() {
		if err != nil {
			mf.Close()
			jf.Close()
			os.Remove(base + ".md")
			os.Remove(base + ".json")
		}
	}()
	if _, err := mf.WriteString(markdown(r)); err != nil {
		return "", "", err
	}
	if _, err := jf.Write(append(body, '\n')); err != nil {
		return "", "", err
	}
	if err := mf.Close(); err != nil {
		return "", "", err
	}
	return md, js, jf.Close()
}

func markdown(r Run) string {
	var b strings.Builder
	fmt.Fprintf(&b, "# API benchmark %s\n\n", r.ID)
	fmt.Fprintf(&b, "- Host: %s\n- Start (UTC): %s\n- End (UTC): %s\n", r.Host, r.Start.UTC().Format(time.RFC3339), r.End.UTC().Format(time.RFC3339))
	fmt.Fprintf(&b, "- Endpoint: %s\n- Cluster state: %s\n", r.Endpoint, r.ClusterState)
	for _, c := range r.Clients {
		if c.Ready == nil {
			fmt.Fprintf(&b, "- %s: no ready line\n", c.Client)
		} else {
			cleanup := "missing"
			if c.Done != nil {
				cleanup = strconv.FormatBool(c.Done.Cleanup)
			}
			fmt.Fprintf(&b, "- %s: %s %s, runtime %s, database %s, setup %.1f ms, auth %.1f ms, cleanup %s\n", c.Client, c.Ready.SDK, c.Ready.SDKVersion, c.Ready.Runtime, c.Ready.Database, c.Ready.SetupMS, c.Ready.AuthMS, cleanup)
		}
		if c.Error != "" {
			fmt.Fprintf(&b, "  - error: %s\n", c.Error)
		}
	}
	fmt.Fprintf(&b, "\n## Network probe\n\n| check | min ms | median ms |\n| --- | --- | --- |\n")
	fmt.Fprintf(&b, "| TCP connect | %.2f | %.2f |\n| TLS handshake | %.2f | %.2f |\n| GET /healthz | %.2f | %.2f |\n",
		r.Probe.TCPConnectMS.Min, r.Probe.TCPConnectMS.Median, r.Probe.TLSHandshakeMS.Min, r.Probe.TLSHandshakeMS.Median, r.Probe.HealthzMS.Min, r.Probe.HealthzMS.Median)
	fmt.Fprintf(&b, "\n## Startup (5 launches, min / median)\n\n| client | wall ms | init ms | max RSS MB |\n| --- | --- | --- | --- |\n")
	for _, s := range r.Startup {
		fmt.Fprintf(&b, "| %s | %.1f / %.1f | %.2f / %.2f | %.1f / %.1f |\n", s.Client, s.WallMS.Min, s.WallMS.Median, s.InitMS.Min, s.InitMS.Median, s.MaxRSSMB.Min, s.MaxRSSMB.Median)
	}
	fmt.Fprintf(&b, "\n## Results\n")
	names := map[string]bool{}
	for _, c := range r.Clients {
		for _, x := range c.Results {
			names[x.Scenario] = true
		}
	}
	for _, name := range slices.Sorted(maps.Keys(names)) {
		fmt.Fprintf(&b, "\n### %s\n\n| client | transport | payload | mode | conc | rate | req/s | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms | err %% | statuses | MB out | MB in | CPU %% | RSS MB | stopped |\n", name)
		fmt.Fprintf(&b, "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n")
		for _, c := range r.Clients {
			for _, x := range c.Results {
				if x.Scenario == name {
					writeRow(&b, c.Client, x)
				}
			}
		}
	}
	return b.String()
}

func writeRow(b *strings.Builder, client string, x Result) {
	rate, cpu := "-", 0.0
	if x.Mode == "open" {
		rate = strconv.Itoa(x.Rate)
	}
	if x.DurationS > 0 {
		cpu = x.CPUS / x.DurationS * 100
	}
	stopped := x.Stopped
	if stopped == "" {
		stopped = "-"
	}
	fmt.Fprintf(b, "| %s | %s | %d | %s | %d | %s | %.1f | %.2f | %.2f | %.2f | %.2f | %.2f | %.2f | %s | %.2f | %.2f | %.1f | %.1f | %s |\n",
		client, x.Transport, x.PayloadBytes, x.Mode, x.Concurrency, rate, x.RPS,
		x.Latency.P50/1000, x.Latency.P90/1000, x.Latency.P99/1000, x.Latency.P999/1000, x.Latency.Max/1000,
		x.ErrorRate*100, statusText(x.Status), float64(x.BytesOut)/(1<<20), float64(x.BytesIn)/(1<<20), cpu, x.RSSMB, stopped)
}

func statusText(m map[string]int64) string {
	if len(m) == 0 {
		return "-"
	}
	var parts []string
	for _, k := range slices.Sorted(maps.Keys(m)) {
		parts = append(parts, fmt.Sprintf("%s:%d", k, m[k]))
	}
	return strings.Join(parts, " ")
}
