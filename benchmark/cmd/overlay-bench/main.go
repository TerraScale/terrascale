// Command overlay-bench measures how fast a region's own write reaches a live shape tail in that region (PIVOT F7).
// It runs on a host in a replica region and sends every request through this region's edge: a live tail parks with
// local=<digest>, then one row is written through the same region (which forwards it to the home). Each sample records
// the write's round trip, the time from its acknowledgement to the tail answering with the row as provisional, and to
// a tail answer that holds the row as confirmed with no provisional copy left. Credentials ({"apiKey","project"}) come
// on stdin. See benchmark/bench/ for results.
package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/base64"
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
)

const authURL = "https://auth.terrascale.tech/terrascale/oauth/token"

type credentials struct {
	APIKey  string `json:"apiKey"`
	Project string `json:"project"`
}

type client struct {
	creds    credentials
	api      string
	http     *http.Client
	mu       sync.Mutex
	token    string
	expires  time.Time
	database string
}

type op struct {
	Op  string         `json:"op"`
	Key string         `json:"key"`
	Row map[string]any `json:"row"`
}

type page struct {
	Handle     string `json:"handle"`
	NextOffset string `json:"next_offset"`
	UpToDate   bool   `json:"up_to_date"`
	Changes    []struct {
		Offset    string   `json:"offset"`
		Ops       []op     `json:"ops"`
		Confirmed []string `json:"confirmed"`
		Revert    []string `json:"revert"`
	} `json:"changes"`
	Provisional []struct {
		ID  string `json:"id"`
		Ops []op   `json:"ops"`
	} `json:"provisional"`
	Local *string `json:"local"`
}

type sample struct {
	Repeat               int     `json:"repeat"`
	Write                int     `json:"write"`
	WriteMs              float64 `json:"writeMs"`
	AckToProvisionalMs   float64 `json:"ackToProvisionalMs"`
	AckToConfirmedMs     float64 `json:"ackToConfirmedMs"`
	ProvisionalFirst     bool    `json:"provisionalFirst"`
	ProvisionalKeyIsHome bool    `json:"provisionalKeyIsHome"`
}

func main() {
	api := flag.String("api", "https://api.terrascale.tech", "public API base URL")
	dial := flag.String("dial", "127.0.0.1:443", "address of this region's edge (OCI has no hairpin NAT)")
	home := flag.String("home", "gru", "home region of the database")
	region := flag.String("region", "", "this replica region (sjc, fra)")
	writes := flag.Int("writes", 50, "writes per repeat")
	repeats := flag.Int("repeats", 3, "measured repeats")
	out := flag.String("out", "", "write every sample as JSON lines to this file")
	flag.Parse()
	if *region == "" {
		fail(errors.New("-region is required"))
	}

	var creds credentials
	if err := json.NewDecoder(os.Stdin).Decode(&creds); err != nil || creds.APIKey == "" || creds.Project == "" {
		fail(errors.New("stdin must hold {\"apiKey\",\"project\"}"))
	}

	ctx, cancel := context.WithTimeout(context.Background(), time.Hour)
	defer cancel()
	c := &client{creds: creds, api: strings.TrimRight(*api, "/"), http: newHTTP(*dial)}
	if err := c.createDatabase(ctx, fmt.Sprintf("overlay-%s-%d", *region, time.Now().Unix()), *home, *region); err != nil {
		fail(err)
	}

	err := c.measure(ctx, *writes, *repeats, *out)
	if deleteErr := c.deleteDatabase(context.Background()); deleteErr != nil {
		fmt.Fprintln(os.Stderr, "overlay-bench: delete database:", deleteErr)
	}

	if err != nil {
		fail(err)
	}
}

func (c *client) measure(ctx context.Context, writes, repeats int, out string) error {
	if _, err := c.exec(ctx, "CREATE TABLE items (id TEXT PRIMARY KEY, v INTEGER NOT NULL)"); err != nil {
		return err
	}

	handle, offset, err := c.bootstrap(ctx)
	if err != nil {
		return err
	}

	var file *os.File
	if out != "" {
		if file, err = os.Create(out); err != nil {
			return err
		}

		defer file.Close()
	}

	local := "0"
	for repeat := 1; repeat <= repeats; repeat++ {
		var samples []sample
		for write := 1; write <= writes; write++ {
			s, next, digest, err := c.sample(ctx, handle, offset, local, fmt.Sprintf("r%dw%04d", repeat, write))
			if err != nil {
				return fmt.Errorf("repeat %d write %d: %w", repeat, write, err)
			}

			s.Repeat, s.Write = repeat, write
			offset, local = next, digest
			samples = append(samples, s)
			if file != nil {
				line, _ := json.Marshal(s)
				_, _ = file.Write(append(line, '\n'))
			}
		}

		pick := func(f func(sample) float64) []float64 {
			values := make([]float64, 0, len(samples))
			for _, s := range samples {
				values = append(values, f(s))
			}

			return values
		}

		first := 0
		for _, s := range samples {
			if s.ProvisionalFirst {
				first++
			}
		}

		fmt.Printf("| %d | %d | %d | %.1f | %.1f | %.1f | %.1f | %.1f | %.1f |\n", repeat, len(samples), first,
			pct(pick(func(s sample) float64 { return s.WriteMs }), 50),
			pct(pick(func(s sample) float64 { return s.AckToProvisionalMs }), 50), pct(pick(func(s sample) float64 { return s.AckToProvisionalMs }), 95),
			pct(pick(func(s sample) float64 { return s.AckToProvisionalMs }), 99),
			pct(pick(func(s sample) float64 { return s.AckToConfirmedMs }), 50), pct(pick(func(s sample) float64 { return s.AckToConfirmedMs }), 95))
	}

	return nil
}

// sample parks a live tail, writes one row through this region and times both tail answers.
func (c *client) sample(ctx context.Context, handle, offset, local, id string) (sample, string, string, error) {
	type answer struct {
		page page
		at   time.Time
		err  error
	}

	parked := make(chan answer, 1)
	go func() {
		p, err := c.live(ctx, handle, offset, local)
		parked <- answer{p, time.Now(), err}
	}()
	time.Sleep(150 * time.Millisecond)
	started := time.Now()
	if _, err := c.exec(ctx, fmt.Sprintf("INSERT INTO items (id, v) VALUES ('%s', 1)", id)); err != nil {
		return sample{}, "", "", err
	}

	acked := time.Now()
	s := sample{WriteMs: ms(acked.Sub(started))}
	first := <-parked
	if first.err != nil {
		return sample{}, "", "", first.err
	}

	p := first.page
	if key, ok := provisional(p, id); ok {
		s.ProvisionalFirst = true
		s.ProvisionalKeyIsHome = !strings.HasPrefix(key, "~")
		s.AckToProvisionalMs = ms(first.at.Sub(acked))
	}

	seen, held := s.ProvisionalFirst, confirmed(p, id)
	for !held || pending(p, id) {
		if p.NextOffset != "" {
			offset = p.NextOffset
		}

		if p.Local != nil {
			local = *p.Local
		}

		next, err := c.live(ctx, handle, offset, local)
		if err != nil {
			return sample{}, "", "", err
		}

		p = next
		held = held || confirmed(p, id)
		if _, ok := provisional(p, id); ok && !seen {
			seen = true
			s.AckToProvisionalMs = ms(time.Since(acked))
		}
	}

	s.AckToConfirmedMs = ms(time.Since(acked))
	if !seen {
		// The pages held the row before any provisional answer: the tail showed it first as confirmed.
		s.AckToProvisionalMs = s.AckToConfirmedMs
	}

	if p.Local != nil {
		local = *p.Local
	}

	return s, p.NextOffset, local, nil
}

func provisional(p page, id string) (string, bool) {
	for _, transaction := range p.Provisional {
		for _, o := range transaction.Ops {
			if o.Row["id"] == id {
				return o.Key, true
			}
		}
	}

	return "", false
}

func pending(p page, id string) bool {
	_, ok := provisional(p, id)
	return ok || p.Local == nil
}

func confirmed(p page, id string) bool {
	for _, change := range p.Changes {
		for _, o := range change.Ops {
			if o.Op == "insert" && o.Row["id"] == id {
				return true
			}
		}
	}

	return false
}

func (c *client) bootstrap(ctx context.Context) (string, string, error) {
	deadline := time.Now().Add(2 * time.Minute)
	path := c.shapePath() + "?offset=-1"
	for {
		var p page
		err := c.call(ctx, http.MethodGet, path, nil, http.StatusOK, &p)
		if err == nil && !strings.HasPrefix(p.NextOffset, "snap.") {
			for !p.UpToDate {
				if err := c.call(ctx, http.MethodGet, c.shapePath()+"?handle="+p.Handle+"&offset="+p.NextOffset, nil, http.StatusOK, &p); err != nil {
					return "", "", err
				}
			}

			return p.Handle, p.NextOffset, nil
		}

		if err == nil {
			path = c.shapePath() + "?handle=" + p.Handle + "&offset=" + p.NextOffset
			continue
		}

		if time.Now().After(deadline) {
			return "", "", err
		}

		time.Sleep(time.Second)
	}
}

func (c *client) live(ctx context.Context, handle, offset, local string) (page, error) {
	var p page
	query := url.Values{"handle": {handle}, "offset": {offset}, "live": {"true"}, "local": {local}}
	for attempt := 0; ; attempt++ {
		err := c.call(ctx, http.MethodGet, c.shapePath()+"?"+query.Encode(), nil, http.StatusOK, &p)
		if err == nil || attempt == 20 {
			return p, err
		}

		time.Sleep(200 * time.Millisecond)
	}
}

func (c *client) shapePath() string {
	return "/v1/projects/" + c.creds.Project + "/databases/" + c.database + "/tables/items/shape"
}

func (c *client) createDatabase(ctx context.Context, name, home, region string) error {
	body, _ := json.Marshal(map[string]any{"name": name, "homeRegion": home, "replicaRegions": []string{region}})
	var created struct {
		ID string `json:"id"`
	}

	if err := c.call(ctx, http.MethodPost, "/v1/projects/"+c.creds.Project+"/databases", body, http.StatusCreated, &created); err != nil {
		return fmt.Errorf("create database: %w", err)
	}

	c.database = created.ID
	return nil
}

func (c *client) deleteDatabase(ctx context.Context) error {
	body, _ := json.Marshal(map[string]any{"replicaRegions": []string{}})
	path := "/v1/projects/" + c.creds.Project + "/databases/" + c.database
	if err := c.call(ctx, http.MethodPatch, path, body, http.StatusOK, nil); err != nil {
		return err
	}

	return c.call(ctx, http.MethodDelete, path, nil, http.StatusNoContent, nil)
}

func (c *client) exec(ctx context.Context, sql string) (int, error) {
	body, _ := json.Marshal(map[string]any{"statements": []map[string]any{{"sql": sql}}})
	var result struct {
		Results []struct {
			Rows [][]any `json:"rows"`
		} `json:"results"`
	}

	if err := c.call(ctx, http.MethodPost, "/v1/projects/"+c.creds.Project+"/databases/"+c.database+"/sql/execute", body, http.StatusOK, &result); err != nil {
		return 0, err
	}

	if len(result.Results) == 0 {
		return 0, nil
	}

	return len(result.Results[0].Rows), nil
}

func (c *client) call(ctx context.Context, method, path string, body []byte, want int, into any) error {
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

	resp, err := c.http.Do(req)
	if err != nil {
		return err
	}

	defer resp.Body.Close()
	data, _ := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	if resp.StatusCode != want {
		return fmt.Errorf("%s %s: %d %s", method, strings.SplitN(path, "?", 2)[0], resp.StatusCode, code(data))
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

	id := c.creds.APIKey
	if strings.HasPrefix(id, "sk_") {
		id = id[3:]
	}
	basic := base64.StdEncoding.EncodeToString([]byte(id[:26] + ":" + c.creds.APIKey))
	req.Header.Set("Authorization", "Basic "+basic)
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	resp, err := newHTTP("").Do(req)
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
		MaxIdleConnsPerHost: 8,
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

func pct(values []float64, p float64) float64 {
	if len(values) == 0 {
		return 0
	}

	sorted := slices.Clone(values)
	slices.Sort(sorted)
	index := int(float64(len(sorted)-1) * p / 100)
	return sorted[index]
}

func ms(d time.Duration) float64 { return float64(d.Microseconds()) / 1000 }

func fail(err error) {
	fmt.Fprintln(os.Stderr, "overlay-bench:", err)
	os.Exit(1)
}
