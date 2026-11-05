// Command replica-lag measures live region replica lag per write, split into stages. It runs on a host in the replica
// region: it writes one row at a time at the home through the public API, polls the local edge until the replica
// returns the row, and (with -nats) records when this region's NATS server delivered the home's revision notice.
// Credentials ({"apiKey","project"}) come on stdin; the optional NATS token from a file. See benchmark/bench/ for results.
package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/nats-io/nats.go"
)

const authURL = "https://auth.terrascale.tech/terrascale/oauth/token"

type credentials struct {
	APIKey  string `json:"apiKey"`
	Project string `json:"project"`
}

type client struct {
	creds    credentials
	api      string
	write    *http.Client
	read     *http.Client
	mu       sync.Mutex
	token    string
	expires  time.Time
	database string
}

type notice struct {
	CommittedAt int64   `json:"committedAt"`
	Revision    *string `json:"revision"`
	Base        *string `json:"base"`
	DatabaseID  string  `json:"databaseId"`
}

type arrival struct {
	at        time.Time
	committed time.Time
	pages     bool
}

type sample struct {
	Repeat          int     `json:"repeat"`
	Write           int     `json:"write"`
	WriteMs         float64 `json:"writeMs"`
	AckToVisibleMs  float64 `json:"ackToVisibleMs"`
	CommitToNotice  float64 `json:"commitToNoticeMs,omitempty"`
	NoticeToVisible float64 `json:"noticeToVisibleMs,omitempty"`
	CommitToVisible float64 `json:"commitToVisibleMs,omitempty"`
	Pages           bool    `json:"pages,omitempty"`
}

func main() {
	api := flag.String("api", "https://api.terrascale.tech", "public API base URL")
	readDial := flag.String("read-dial", "127.0.0.1:443", "address of this region's edge for replica reads (OCI has no hairpin NAT)")
	home := flag.String("home", "gru", "home region of the database")
	region := flag.String("region", "", "this replica region (sjc, fra)")
	writes := flag.Int("writes", 500, "writes per repeat")
	repeats := flag.Int("repeats", 3, "measured repeats")
	natsURL := flag.String("nats", "", "this region's NATS URL, to time notice arrival (optional)")
	natsTokenFile := flag.String("nats-token-file", "", "file holding the TerraBase NATS token")
	trace := flag.String("trace", "", "write every notice (arrival, revision, base, bytes) and write timing as JSON lines to this file")
	out := flag.String("out", "", "write every sample as JSON lines to this file")
	flag.Parse()
	if *region == "" {
		fail(errors.New("-region is required"))
	}

	var creds credentials
	if err := json.NewDecoder(os.Stdin).Decode(&creds); err != nil || creds.APIKey == "" || creds.Project == "" {
		fail(errors.New("stdin must hold {\"apiKey\",\"project\"}"))
	}

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Hour)
	defer cancel()
	c := &client{creds: creds, api: strings.TrimRight(*api, "/"), write: newHTTP(""), read: newHTTP(*readDial)}
	name := fmt.Sprintf("lag-%s-%d", *region, time.Now().Unix())
	if err := c.createDatabase(ctx, name, *home, *region); err != nil {
		fail(err)
	}

	if *trace != "" {
		f, err := os.OpenFile(*trace, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
		if err != nil {
			fail(err)
		}

		defer f.Close()
		traceOut = f
	}

	err := c.measure(ctx, *home, *region, *writes, *repeats, *natsURL, *natsTokenFile, *out)
	if deleteErr := c.deleteDatabase(context.Background()); deleteErr != nil {
		fmt.Fprintln(os.Stderr, "replica-lag: delete database:", deleteErr)
	}

	if err != nil {
		fail(err)
	}
}

func (c *client) measure(ctx context.Context, home, region string, writes, repeats int, natsURL, natsTokenFile, out string) error {
	arrivals := make(chan arrival, 1024)
	if natsURL != "" {
		token, err := os.ReadFile(natsTokenFile)
		if err != nil {
			return fmt.Errorf("NATS token file: %w", err)
		}

		conn, err := nats.Connect(natsURL, nats.Token(strings.TrimSpace(string(token))), nats.Name("replica-lag-bench"))
		if err != nil {
			return fmt.Errorf("NATS: %w", err)
		}

		defer conn.Close()
		on := func(m *nats.Msg) {
			at := time.Now()
			if n, ok := readNotice(m.Data); ok && n.DatabaseID == c.database {
				traceLine(map[string]any{"notice": at.UnixMicro(), "committedAt": n.CommittedAt, "revision": n.Revision, "base": n.Base, "bytes": len(m.Data)})
				select {
				case arrivals <- arrival{at: at, committed: time.UnixMilli(n.CommittedAt), pages: n.Base != nil}:
				default:
				}
			}
		}

		for _, subject := range []string{fmt.Sprintf("terrabase.revision.%s.%s.%s", region, home, c.database), fmt.Sprintf("terrabase.revision.%s.%s", home, c.database)} {
			if _, err := conn.Subscribe(subject, on); err != nil {
				return err
			}
		}
	}

	if _, err := c.exec(ctx, c.write, "CREATE TABLE lag (id INTEGER PRIMARY KEY, at INTEGER NOT NULL)"); err != nil {
		return fmt.Errorf("create table: %w", err)
	}

	if err := c.visible(ctx, "SELECT count(*) FROM lag", 2*time.Minute); err != nil {
		return fmt.Errorf("replica never served the table: %w", err)
	}

	var sink io.Writer = io.Discard
	if out != "" {
		f, err := os.OpenFile(out, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
		if err != nil {
			return err
		}

		defer f.Close()
		sink = f
	}

	fmt.Printf("| repeat | writes | ack→visible p50 | p99 | max | commit→visible p50 | p99 | commit→notice p50 | p99 | notice→visible p50 | p99 | pushed |\n")
	fmt.Printf("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n")
	id := 0
	for r := 1; r <= repeats; r++ {
		var samples []sample
		for i := 1; i <= writes; i++ {
			id++
			s, err := c.one(ctx, id, arrivals)
			if err != nil {
				return fmt.Errorf("repeat %d write %d: %w", r, i, err)
			}

			s.Repeat, s.Write = r, i
			samples = append(samples, s)
			line, _ := json.Marshal(s)
			fmt.Fprintln(sink, string(line))
		}

		report(r, samples)
	}

	return nil
}

func (c *client) one(ctx context.Context, id int, arrivals chan arrival) (sample, error) {
	for len(arrivals) > 0 {
		<-arrivals
	}

	var sent time.Time
	for attempt := 1; ; attempt++ {
		sent = time.Now()
		_, err := c.exec(ctx, c.write, fmt.Sprintf("INSERT INTO lag (id, at) VALUES (%d, %d)", id, sent.UnixMilli()))
		if err == nil || strings.Contains(err.Error(), "unique_violation") {
			break
		}

		if attempt == 3 || !strings.Contains(err.Error(), ": 5") {
			return sample{}, err
		}

		time.Sleep(200 * time.Millisecond)
	}

	ack := time.Now()
	query := fmt.Sprintf("SELECT id FROM lag WHERE id = %d", id)
	deadline := time.Now().Add(30 * time.Second)
	for {
		before := time.Now()
		rows, err := c.exec(ctx, c.read, query)
		traceLine(map[string]any{"poll": id, "at": before.UnixMicro(), "ms": ms(time.Since(before)), "rows": rows, "error": err != nil, "headers": lastHeaders})
		if err == nil && rows > 0 {
			break
		}

		if time.Now().After(deadline) {
			return sample{}, errors.New("not visible on the replica within 30 s")
		}

		time.Sleep(time.Millisecond)
	}

	seen := time.Now()
	traceLine(map[string]any{"write": id, "sent": sent.UnixMicro(), "ack": ack.UnixMicro(), "visible": seen.UnixMicro()})
	s := sample{WriteMs: ms(ack.Sub(sent)), AckToVisibleMs: ms(seen.Sub(ack))}
	for {
		select {
		case a := <-arrivals:
			if a.at.Before(sent) {
				continue
			}

			s.CommitToNotice, s.NoticeToVisible, s.CommitToVisible, s.Pages = ms(a.at.Sub(a.committed)), ms(seen.Sub(a.at)), ms(seen.Sub(a.committed)), a.pages
			return s, nil
		default:
			return s, nil
		}
	}
}

func report(repeat int, samples []sample) {
	pick := func(f func(sample) float64) []float64 {
		var v []float64
		for _, s := range samples {
			if x := f(s); x != 0 {
				v = append(v, x)
			}
		}

		slices.Sort(v)
		return v
	}

	lag := pick(func(s sample) float64 { return s.AckToVisibleMs })
	commit := pick(func(s sample) float64 { return s.CommitToVisible })
	notice := pick(func(s sample) float64 { return s.CommitToNotice })
	apply := pick(func(s sample) float64 { return s.NoticeToVisible })
	pushed := 0
	for _, s := range samples {
		if s.Pages {
			pushed++
		}
	}

	fmt.Printf("| %d | %d | %.1f | %.1f | %.1f | %s | %s | %s | %s | %s | %s | %d |\n", repeat, len(samples), pct(lag, 0.5), pct(lag, 0.99), pct(lag, 1),
		cell(commit, 0.5), cell(commit, 0.99), cell(notice, 0.5), cell(notice, 0.99), cell(apply, 0.5), cell(apply, 0.99), pushed)
}

func pct(v []float64, p float64) float64 {
	if len(v) == 0 {
		return 0
	}

	i := int(float64(len(v))*p+0.999999) - 1
	return v[max(0, min(i, len(v)-1))]
}

func cell(v []float64, p float64) string {
	if len(v) == 0 {
		return "—"
	}

	return fmt.Sprintf("%.1f", pct(v, p))
}

func readNotice(data []byte) (notice, bool) {
	var out notice
	if json.Unmarshal(data, &out) == nil {
		return out, true
	}

	if len(data) < 4 {
		return notice{}, false
	}

	n := int(binary.BigEndian.Uint32(data[len(data)-4:]))
	if n > len(data)-4 {
		return notice{}, false
	}

	err := json.Unmarshal(data[len(data)-4-n:len(data)-4], &out)
	return out, err == nil
}

func (c *client) createDatabase(ctx context.Context, name, home, region string) error {
	body, _ := json.Marshal(map[string]any{"name": name, "homeRegion": home, "replicaRegions": []string{region}})
	var created struct {
		ID string `json:"id"`
	}

	if err := c.call(ctx, c.write, http.MethodPost, "/v1/projects/"+c.creds.Project+"/databases", body, http.StatusCreated, &created); err != nil {
		return fmt.Errorf("create database: %w", err)
	}

	c.database = created.ID
	return nil
}

func (c *client) deleteDatabase(ctx context.Context) error {
	body, _ := json.Marshal(map[string]any{"replicaRegions": []string{}})
	path := "/v1/projects/" + c.creds.Project + "/databases/" + c.database
	if err := c.call(ctx, c.write, http.MethodPatch, path, body, http.StatusOK, nil); err != nil {
		return err
	}

	return c.call(ctx, c.write, http.MethodDelete, path, nil, http.StatusNoContent, nil)
}

func (c *client) exec(ctx context.Context, h *http.Client, sql string) (int, error) {
	body, _ := json.Marshal(map[string]any{"statements": []map[string]any{{"sql": sql}}})
	var result struct {
		Results []struct {
			Rows [][]any `json:"rows"`
		} `json:"results"`
	}

	if err := c.call(ctx, h, http.MethodPost, "/v1/projects/"+c.creds.Project+"/databases/"+c.database+"/sql/execute", body, http.StatusOK, &result); err != nil {
		return 0, err
	}

	if len(result.Results) == 0 {
		return 0, nil
	}

	return len(result.Results[0].Rows), nil
}

func (c *client) visible(ctx context.Context, sql string, within time.Duration) error {
	deadline := time.Now().Add(within)
	for {
		_, err := c.exec(ctx, c.read, sql)
		if err == nil {
			return nil
		}

		if time.Now().After(deadline) {
			return err
		}

		time.Sleep(100 * time.Millisecond)
	}
}

func (c *client) call(ctx context.Context, h *http.Client, method, path string, body []byte, want int, into any) error {
	token, err := c.accessToken(ctx)
	if err != nil {
		return err
	}

	req, err := http.NewRequestWithContext(ctx, method, c.api+path, bytes.NewReader(body))
	if err != nil {
		return err
	}

	req.Header.Set("Authorization", "Bearer "+token)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}

	resp, err := h.Do(req)
	if err != nil {
		return err
	}

	defer resp.Body.Close()
	data, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if h == c.read {
		lastHeaders = resp.Header.Get("X-Terrabase-Machine") + " " + resp.Header.Get("Server-Timing") + " " + resp.Header.Get("TerraScale-Write-Route") + " " + resp.Header.Get("Ts-Replay") + " " + resp.Header.Get("Via")
	}
	if resp.StatusCode != want {
		return fmt.Errorf("%s %s: %d %s", method, path, resp.StatusCode, code(data))
	}

	if into != nil {
		return json.Unmarshal(data, into)
	}

	return nil
}

func code(data []byte) string {
	var e struct {
		Error struct {
			Code string `json:"code"`
		} `json:"error"`
	}

	_ = json.Unmarshal(data, &e)
	return e.Error.Code
}

func (c *client) accessToken(ctx context.Context) (string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.token != "" && time.Now().Before(c.expires) {
		return c.token, nil
	}

	form := url.Values{"grant_type": {"client_credentials"}, "project": {c.creds.Project}}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, authURL, strings.NewReader(form.Encode()))
	if err != nil {
		return "", err
	}

	basic := base64.StdEncoding.EncodeToString([]byte(c.creds.APIKey[:26] + ":" + c.creds.APIKey))
	req.Header.Set("Authorization", "Basic "+basic)
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	resp, err := c.write.Do(req)
	if err != nil {
		return "", err
	}

	defer resp.Body.Close()
	var t struct {
		AccessToken string `json:"access_token"`
		ExpiresIn   int    `json:"expires_in"`
	}

	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("token exchange: %d", resp.StatusCode)
	}

	if err := json.NewDecoder(resp.Body).Decode(&t); err != nil || t.AccessToken == "" || t.ExpiresIn <= 0 {
		return "", errors.New("token exchange: no access token")
	}

	c.token = t.AccessToken
	c.expires = time.Now().Add(time.Duration(t.ExpiresIn) * time.Second * 3 / 4)
	return c.token, nil
}

func newHTTP(dial string) *http.Client {
	dialer := &net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}
	transport := &http.Transport{
		ForceAttemptHTTP2:   true,
		TLSClientConfig:     &tls.Config{MinVersion: tls.VersionTLS12},
		MaxIdleConnsPerHost: 4,
		IdleConnTimeout:     90 * time.Second,
		DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
			if dial != "" {
				address = dial
			}

			return dialer.DialContext(ctx, network, address)
		},
	}

	return &http.Client{Transport: transport, Timeout: 30 * time.Second}
}

var (
	lastHeaders string
	traceOut    io.Writer
	traceMu     sync.Mutex
)

func traceLine(v map[string]any) {
	if traceOut == nil {
		return
	}

	line, _ := json.Marshal(v)
	traceMu.Lock()
	defer traceMu.Unlock()
	fmt.Fprintln(traceOut, string(line))
}

func ms(d time.Duration) float64 { return float64(d.Microseconds()) / 1000 }

func fail(err error) {
	fmt.Fprintln(os.Stderr, "replica-lag:", err)
	os.Exit(1)
}
