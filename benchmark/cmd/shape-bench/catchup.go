package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"os"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/benchclient"
)

const (
	catchupJoinS = 10
	sealPace     = 50 * time.Millisecond
	mirrorWait   = 2 * time.Minute
	cdnHost      = ".terrascale.tech"
)

type catchupStep struct {
	Tier                          string
	N, Repeat, Chunks             int
	Reads                         map[string]int64
	Sealed, Mirrored              int64
	MirroredAhead                 int
	Origin, Manifests, ObjectGets int64
	EdgeHits, EdgeMisses          int64
	Locations                     map[string][2]int64
	Start, End                    time.Time
	OriginBytes, ObjectBytes      int64
	Failures                      int64
	Error                         string `json:",omitempty"`
	mu                            sync.Mutex
}

func (s *catchupStep) add(field *int64, v int64) { atomic.AddInt64(field, v) }

func (s *catchupStep) fail(err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.Failures++
	if s.Error == "" {
		s.Error = err.Error()
	}
}

func catchup(args []string) error {
	fs := flag.NewFlagSet("catchup", flag.ContinueOnError)
	tiers := fs.String("tiers", "t0,t1,t2", "tiers to run")
	readers := fs.String("readers", "1,10,100,1000", "readers per step")
	chunks := fs.Int("chunks", 100, "sealed chunks behind the snapshot")
	repeats := fs.Int("repeats", 3, "repeats of every step")
	out := fs.String("out", "", "report base path (writes .md and .json)")
	origin := fs.String("origin", "", "a running node's client URL (empty: the local host); its key is TS_BENCH_API_KEY")
	project, database := fs.String("project", "", "project id, with -origin"), fs.String("database", "", "database id, with -origin")
	metrics := fs.String("metrics", "", "comma-separated /metrics URLs of the nodes, with -origin")
	if err := fs.Parse(args); err != nil {
		return err
	}
	var ns []int
	for s := range strings.SplitSeq(*readers, ",") {
		n, err := strconv.Atoi(s)
		if err != nil || n < 1 {
			return fmt.Errorf("-readers: %q", s)
		}
		ns = append(ns, n)
	}
	ts := strings.Split(*tiers, ",")
	if slices.ContainsFunc(ts, func(t string) bool { return t != "t0" && t != "t1" && t != "t2" }) {
		return fmt.Errorf("-tiers: %q", *tiers)
	}
	ctx := context.Background()
	var t benchclient.Targets
	var nodes []string
	if *origin != "" {

		t = benchclient.Targets{Project: *project, Database: *database, Regions: map[string]benchclient.RegionTarget{region: {URL: *origin, Token: os.Getenv("TS_BENCH_API_KEY")}}}
		nodes = strings.Split(*metrics, ",")
		if *project == "" || *database == "" || *metrics == "" || t.Regions[region].Token == "" {
			return fmt.Errorf("-origin needs -project, -database, -metrics and TS_BENCH_API_KEY")
		}
	} else {
		if os.Getenv("BUCKET_NAME") == "" && slices.ContainsFunc(ts, func(t string) bool { return t != "t0" }) {
			return fmt.Errorf("t1 and t2 need a bucket: BUCKET_NAME and the AWS_* variables of a private ts-* test bucket")
		}
		work, err := os.MkdirTemp("", "shape-bench-")
		if err != nil {
			return err
		}
		defer os.RemoveAll(work)
		h, err := startHost(ctx, work)
		if err != nil {
			return err
		}
		defer h.stop()
		t = h.targets
		if nodes, _, err = metricsListeners(ctx, t.PID); err != nil {
			return err
		}
	}
	w, err := benchclient.NewRegion(region, t, writeRate*4)
	if err != nil {
		return err
	}
	b := &bench{origin: strings.TrimSuffix(t.Regions[region].URL, "/"), token: t.Regions[region].Token, writer: w,
		shape: &http.Client{Timeout: 2 * time.Minute, Transport: &http.Transport{MaxIdleConnsPerHost: 10_000}, CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		}}}
	b.database = b.origin + "/v1/projects/" + t.Project + "/databases/" + t.Database
	objects := &http.Client{Timeout: time.Minute, Transport: &http.Transport{MaxIdleConnsPerHost: 1000}}
	var steps []*catchupStep
	for r := range *repeats {
		for _, tier := range ts {
			for _, n := range ns {
				s := &catchupStep{Tier: tier, N: n, Repeat: r + 1, Chunks: *chunks}
				if err := runCatchup(ctx, b, objects, nodes, s); err != nil {
					s.fail(err)
				}
				fmt.Fprintf(os.Stderr, "catchup %s N=%d #%d: shape_body %d origin %d objects %d hits %d %s\n",
					tier, n, r+1, s.Reads["shape_body"], s.Origin, s.ObjectGets, s.EdgeHits, s.Error)
				steps = append(steps, s)
			}
		}
	}
	if *out == "" {
		*out = fmt.Sprintf("%s/%s-catchup-%s", reportDir, time.Now().UTC().Format(time.DateOnly), ulid.Make())
	}
	if err := benchclient.WriteJSON(*out+".json", steps); err != nil {
		return err
	}
	if err := os.WriteFile(*out+".md", []byte(catchupTable(steps)), 0o644); err != nil {
		return err
	}
	fmt.Println(*out + ".md")
	return nil
}

func runCatchup(ctx context.Context, b *bench, objects *http.Client, nodes []string, s *catchupStep) error {
	b.collection = "cu-" + strings.ToLower(ulid.Make().String())
	body, _ := json.Marshal(map[string]string{"name": b.collection})
	if resp, err := b.writer.Do(ctx, http.MethodPost, "/collections", body, "application/json"); err != nil || resp.Status/100 != 2 {
		return fmt.Errorf("create collection: %v HTTP %d %s", err, resp.Status, resp.Body)
	}
	m := &measure{}
	if err := b.writeOne(ctx); err != nil {
		return err
	}
	boot, err := b.retryGet(ctx, m, b.shapeURL(url.Values{"offset": {"-1"}}))
	if err != nil || boot.status != http.StatusTemporaryRedirect {
		return fmt.Errorf("bootstrap: %v HTTP %d %s", err, boot.status, boot.body)
	}
	handle, first := boot.header.Get("TB-Shape-Handle"), b.origin+boot.header.Get("Location")
	page, err := b.retryGet(ctx, m, first)
	if err != nil || page.status != http.StatusOK {
		return fmt.Errorf("snapshot page: %v HTTP %d", err, page.status)
	}
	offset := page.header.Get("TB-Shape-Next-Offset")
	for sealed := 0; sealed < s.Chunks; {
		if err := b.writeOne(ctx); err != nil {
			return err
		}

		next, err := b.retryGet(ctx, m, b.shapeURL(url.Values{"handle": {handle}, "offset": {offset}}))
		if err != nil || next.status != http.StatusOK {
			return fmt.Errorf("seal chunk: %v HTTP %d %s", err, next.status, next.body)
		}
		if at := next.header.Get("TB-Shape-Next-Offset"); at != offset {
			offset, sealed = at, sealed+1
		}
		sleep(ctx, sealPace)
	}
	head := offset
	if s.Tier != "t0" {
		deadline := time.Now().Add(mirrorWait)
		for {
			if s.MirroredAhead, err = b.mirrored(ctx, handle, first, head); err != nil {
				return err
			}
			if s.MirroredAhead == s.Chunks+1 || time.Now().After(deadline) {
				break
			}
			sleep(ctx, time.Second)
		}
	}
	before := scrapeAll(ctx, nodes)
	s.Start = time.Now().UTC()
	var wg sync.WaitGroup
	for range s.N {
		sleep(ctx, time.Duration(catchupJoinS)*time.Second/time.Duration(s.N))
		wg.Go(func() {
			if err := b.catchUp(ctx, objects, s, handle, first, head); err != nil {
				s.fail(err)
			}
		})
	}
	wg.Wait()
	s.End = time.Now().UTC()
	sleep(ctx, 2*time.Second)
	after := scrapeAll(ctx, nodes)
	s.Reads = map[string]int64{}
	for i := range nodes {
		r0, err := storageReads(before[i])
		if err != nil {
			return err
		}
		r1, err := storageReads(after[i])
		if err != nil {
			return err
		}
		for p, v := range r1 {
			s.Reads[p] += int64(v - r0[p])
		}
		counter := func(body, base string) int64 { v, _ := promValue(body, base, ""); return int64(v) }
		s.Sealed += counter(after[i], "terrabase_shape_seals") - counter(before[i], "terrabase_shape_seals")
		s.Mirrored += counter(after[i], "terrabase_shape_mirror_lag_seconds_count") - counter(before[i], "terrabase_shape_mirror_lag_seconds_count")
	}
	return nil
}

func (b *bench) retryGet(ctx context.Context, m *measure, target string) (shapeResponse, error) {
	for range 100 {
		resp, err := b.shapeGet(ctx, m, target)
		if err != nil || (resp.status != http.StatusTooManyRequests && resp.status != http.StatusServiceUnavailable) {
			return resp, err
		}
		sleep(ctx, shapeRetryWait)
	}
	return shapeResponse{}, fmt.Errorf("%s: still busy after 100 tries", target)
}

func (b *bench) writeOne(ctx context.Context) error {
	for range 10 {
		resp, err := b.writer.Do(ctx, http.MethodPut, "/collections/"+b.collection+"/documents/by-key?id="+ulid.Make().String(), document(time.Now()), "application/json")
		if err == nil && (resp.Status == http.StatusTooManyRequests || resp.Status == http.StatusServiceUnavailable) {
			sleep(ctx, shapeRetryWait)
			continue
		}
		if err != nil || resp.Status/100 != 2 {
			return fmt.Errorf("write: %v HTTP %d %s", err, resp.Status, resp.Body)
		}
		return nil
	}
	return fmt.Errorf("write: still busy after 10 tries")
}

type manifestObject struct {
	Offset     string `json:"offset"`
	NextOffset string `json:"next_offset"`
	URL        string `json:"url"`
	Sha256     string `json:"sha256"`
	Size       int64  `json:"size"`
}

func (b *bench) manifest(ctx context.Context, handle, offset string) ([]manifestObject, error) {
	resp, err := b.retryGet(ctx, &measure{}, b.database+"/collections/"+b.collection+"/shape/manifest?"+url.Values{"handle": {handle}, "offset": {offset}}.Encode())
	if err != nil || resp.status != http.StatusOK {
		return nil, fmt.Errorf("manifest: %v HTTP %d %s", err, resp.status, resp.body)
	}
	var m struct{ Objects []manifestObject }
	return m.Objects, json.Unmarshal(resp.body, &m)
}

func (b *bench) mirrored(ctx context.Context, handle, first, head string) (int, error) {
	offset, count := offsetOf(first), 0
	for offset != head {
		objs, err := b.manifest(ctx, handle, offset)
		if err != nil || len(objs) == 0 {
			return count, err
		}
		count += len(objs)
		offset = objs[len(objs)-1].NextOffset
	}
	return count, nil
}

func offsetOf(target string) string {
	u, _ := url.Parse(target)
	return u.Query().Get("offset")
}

func (b *bench) catchUp(ctx context.Context, objects *http.Client, s *catchupStep, handle, first, head string) error {
	m := &measure{}
	offset := offsetOf(first)
	for offset != head {
		if s.Tier != "t0" {
			objs, err := b.manifest(ctx, handle, offset)
			s.add(&s.Manifests, 1)
			if err != nil {
				return err
			}
			if len(objs) > 0 {
				for _, o := range objs {
					if err := fetchObject(ctx, objects, s, o); err != nil {
						return err
					}
				}
				offset = objs[len(objs)-1].NextOffset
				continue
			}
		}
		resp, err := b.shapeGet(ctx, m, b.shapeURL(url.Values{"handle": {handle}, "offset": {offset}}))
		if err != nil {
			return err
		}
		if resp.status != http.StatusOK {
			if resp.status == http.StatusTooManyRequests || resp.status == http.StatusServiceUnavailable {
				sleep(ctx, shapeRetryWait)
				continue
			}
			return fmt.Errorf("origin read at %s: HTTP %d %s", offset, resp.status, resp.body)
		}
		s.add(&s.Origin, 1)
		s.add(&s.OriginBytes, int64(len(resp.body)))
		offset = resp.header.Get("TB-Shape-Next-Offset")
	}
	return nil
}

func fetchObject(ctx context.Context, client *http.Client, s *catchupStep, o manifestObject) error {
	u, err := url.Parse(o.URL)
	if err != nil {
		return err
	}
	if cdn := strings.HasPrefix(u.Host, "shapes-") && strings.HasSuffix(u.Host, cdnHost); cdn != (s.Tier == "t2") {
		return fmt.Errorf("tier %s cannot run on this bucket: its manifest links %s (t2 needs an R2 bucket, t1 any other)", s.Tier, u.Host)
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, o.URL, nil)
	if err != nil {
		return err
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	bytes, err := io.ReadAll(resp.Body)
	if err != nil {
		return err
	}
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("object %s: HTTP %d %s", o.Offset, resp.StatusCode, bytes)
	}
	sum := sha256.Sum256(bytes)
	if int64(len(bytes)) != o.Size || hex.EncodeToString(sum[:]) != o.Sha256 {
		return fmt.Errorf("object %s: size or sha256 differs from the manifest", o.Offset)
	}
	s.add(&s.ObjectGets, 1)
	s.add(&s.ObjectBytes, int64(len(bytes)))
	if s.Tier != "t2" {
		return nil
	}

	hit := resp.Header.Get("Cf-Cache-Status") == "HIT"
	location := resp.Header.Get("Cf-Ray")
	location = location[strings.LastIndexByte(location, '-')+1:]
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Locations == nil {
		s.Locations = map[string][2]int64{}
	}
	counts := s.Locations[location]
	if hit {
		s.EdgeHits++
		counts[0]++
	} else {
		s.EdgeMisses++
		counts[1]++
	}
	s.Locations[location] = counts
	return nil
}

func catchupTable(steps []*catchupStep) string {
	type key struct {
		tier string
		n    int
	}
	groups := map[key][]*catchupStep{}
	var order []key
	for _, s := range steps {
		k := key{s.Tier, s.N}
		if _, ok := groups[k]; !ok {
			order = append(order, k)
		}
		groups[k] = append(groups[k], s)
	}
	slices.SortFunc(order, func(a, b key) int {
		if a.tier != b.tier {
			return strings.Compare(a.tier, b.tier)
		}
		return a.n - b.n
	})
	stat := func(ss []*catchupStep, f func(*catchupStep) float64) string {
		v := make([]float64, len(ss))
		for i, s := range ss {
			v[i] = f(s)
		}
		slices.Sort(v)
		med := v[len(v)/2]
		if len(v)%2 == 0 {
			med = (v[len(v)/2-1] + v[len(v)/2]) / 2
		}
		if v[0] == v[len(v)-1] {
			return trim(med)
		}
		return fmt.Sprintf("%s (%s–%s)", trim(med), trim(v[0]), trim(v[len(v)-1]))
	}
	var b strings.Builder
	b.WriteString("| tier | readers | repeats | objects per reader | mirrored ahead | origin shape_body reads | origin shape reads | shape_body reads per object | origin requests | manifest calls | object GETs | bucket GETs | CDN locations | bucket GETs per object per location | CDN hit ratio | bucket PUTs | origin MB | object MB | failures |\n")
	b.WriteString("|" + strings.Repeat(" --- |", 19) + "\n")
	for _, k := range order {
		ss := groups[k]
		objectsPerReader := float64(ss[0].Chunks + 1)
		fmt.Fprintf(&b, "| %s | %d | %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |\n", k.tier, k.n, len(ss), trim(objectsPerReader),
			stat(ss, func(s *catchupStep) float64 { return float64(s.MirroredAhead) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Reads["shape_body"]) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Reads["shape"]) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Reads["shape_body"]) / objectsPerReader }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Origin) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Manifests) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.ObjectGets) }),
			stat(ss, func(s *catchupStep) float64 { return float64(bucketGets(s)) }),
			stat(ss, func(s *catchupStep) float64 { return float64(len(s.Locations)) }),
			stat(ss, func(s *catchupStep) float64 {
				return math.Round(float64(bucketGets(s))/objectsPerReader/float64(max(len(s.Locations), 1))*1000) / 1000
			}),
			stat(ss, func(s *catchupStep) float64 {
				if t := s.EdgeHits + s.EdgeMisses; t > 0 {
					return math.Round(float64(s.EdgeHits)/float64(t)*1000) / 1000
				}
				return 0
			}),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Mirrored) }),
			stat(ss, func(s *catchupStep) float64 { return math.Round(float64(s.OriginBytes)/1e4) / 100 }),
			stat(ss, func(s *catchupStep) float64 { return math.Round(float64(s.ObjectBytes)/1e4) / 100 }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Failures) }))
	}
	b.WriteString("\nErrors:\n\n")
	for _, s := range steps {
		if s.Error != "" {
			fmt.Fprintf(&b, "- %s N=%d #%d: %s\n", s.Tier, s.N, s.Repeat, s.Error)
		}
	}
	return b.String()
}

func bucketGets(s *catchupStep) int64 {
	if s.Tier == "t2" {
		return s.EdgeMisses
	}
	return s.ObjectGets
}

func trim(v float64) string { return strconv.FormatFloat(v, 'f', -1, 64) }
