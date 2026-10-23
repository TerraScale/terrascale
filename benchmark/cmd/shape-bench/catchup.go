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
	"terrascale.tech/ts-benchmark/internal/replbench"
)

// The free-read tiers (docs/terrabase/free-reads-edge.md) as a catch-up load (README "Catch-up tiers"): readers that join
// behind the head read the same sealed snapshot page and chunks, from the origin (t0), the bucket (t1) or the shape edge (t2).
const (
	edgePrefix   = "https://shapes.terrascale.tech/" // ShapeObjectStore.Edge
	catchupJoinS = 10                                // readers join evenly over this many seconds
	sealPace     = 50 * time.Millisecond             // one write per 50 ms, 20 per second
	mirrorWait   = 2 * time.Minute
)

// catchupStep is one (tier, readers, repeat) measurement.
type catchupStep struct {
	Tier                          string
	N, Repeat, Chunks             int
	Reads                         map[string]int64 // terrabase.storage.reads delta by path, both nodes
	Sealed, Mirrored              int64            // terrabase.shape.seals and mirror uploads (bucket PUTs) during the readers
	MirroredAhead                 int              // objects the manifest chain listed before the readers started
	Origin, Manifests, ObjectGets int64            // reader requests: origin shape reads, manifest calls, object fetches
	EdgeHits, EdgeMisses          int64            // object fetches the edge answered from cache, or forwarded to the bucket
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
	if os.Getenv("BUCKET_NAME") == "" && slices.ContainsFunc(ts, func(t string) bool { return t != "t0" }) {
		return fmt.Errorf("t1 and t2 need a bucket: BUCKET_NAME and the AWS_* variables of a private ts-*-test bucket")
	}
	ctx := context.Background()
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
	t := h.targets
	nodes, _, err := metricsListeners(ctx, t.PID)
	if err != nil {
		return err
	}
	w, err := replbench.NewRegion(region, t, writeRate*4)
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
	if err := replbench.WriteJSON(*out+".json", steps); err != nil {
		return err
	}
	if err := os.WriteFile(*out+".md", []byte(catchupTable(steps)), 0o644); err != nil {
		return err
	}
	fmt.Println(*out + ".md")
	return nil
}

// runCatchup seals a snapshot and s.Chunks chunks behind it on a new collection (so a new handle and new objects), waits
// for the mirror on t1 and t2, then lets s.N readers join and catch up to the head.
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
		// Reading the head after a write seals one chunk once the write is remote durable: the steady state a live reader
		// drives. Until then the head answers unchanged, and the next write joins the same chunk.
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

// retryGet repeats a request the server refused as busy or unavailable (429, 503), as a client would.
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
	resp, err := b.writer.Do(ctx, http.MethodPut, "/collections/"+b.collection+"/documents/by-key?id="+ulid.Make().String(), document(time.Now()), "application/json")
	if err != nil || resp.Status/100 != 2 {
		return fmt.Errorf("write: %v HTTP %d %s", err, resp.Status, resp.Body)
	}
	return nil
}

type manifestObject struct {
	Offset     string `json:"offset"`
	NextOffset string `json:"next_offset"`
	URL        string `json:"url"`
	Sha256     string `json:"sha256"`
	Size       int64  `json:"size"`
}

// manifest lists the mirrored objects from the absolute shape URL target's offset.
func (b *bench) manifest(ctx context.Context, handle, offset string) ([]manifestObject, error) {
	resp, err := b.retryGet(ctx, &measure{}, b.database+"/collections/"+b.collection+"/shape/manifest?"+url.Values{"handle": {handle}, "offset": {offset}}.Encode())
	if err != nil || resp.status != http.StatusOK {
		return nil, fmt.Errorf("manifest: %v HTTP %d %s", err, resp.status, resp.body)
	}
	var m struct{ Objects []manifestObject }
	return m.Objects, json.Unmarshal(resp.body, &m)
}

// mirrored counts the objects the manifest chain lists from the first page to head.
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

// catchUp reads from the snapshot's first page to head: on t1 and t2 through the manifest's objects while it lists them,
// otherwise from the origin.
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

// fetchObject reads one object (t1 straight from the bucket, t2 through the edge) and checks its size and digest.
func fetchObject(ctx context.Context, client *http.Client, s *catchupStep, o manifestObject) error {
	u := o.URL
	if s.Tier == "t1" {
		if rest, ok := strings.CutPrefix(u, edgePrefix); ok {
			u = "https://" + rest
		}
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
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
	switch resp.Header.Get("Tb-Edge-Cache") {
	case "HIT":
		s.add(&s.EdgeHits, 1)
	case "MISS":
		s.add(&s.EdgeMisses, 1)
	}
	return nil
}

// catchupTable reports the median and range over repeats of every (tier, readers) step.
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
	b.WriteString("| tier | readers | repeats | objects per reader | mirrored ahead | origin shape_body reads | origin shape reads | shape_body reads per object | origin requests | manifest calls | object GETs | bucket GETs | edge hit ratio | bucket PUTs | origin MB | object MB | failures |\n")
	b.WriteString("|" + strings.Repeat(" --- |", 17) + "\n")
	for _, k := range order {
		ss := groups[k]
		objectsPerReader := float64(ss[0].Chunks + 1)
		fmt.Fprintf(&b, "| %s | %d | %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |\n", k.tier, k.n, len(ss), trim(objectsPerReader),
			stat(ss, func(s *catchupStep) float64 { return float64(s.MirroredAhead) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Reads["shape_body"]) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Reads["shape"]) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Reads["shape_body"]) / objectsPerReader }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Origin) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.Manifests) }),
			stat(ss, func(s *catchupStep) float64 { return float64(s.ObjectGets) }),
			stat(ss, func(s *catchupStep) float64 { return float64(bucketGets(s)) }),
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

// bucketGets are the object reads that reached the bucket: every fetch on t1, edge misses on t2.
func bucketGets(s *catchupStep) int64 {
	if s.Tier == "t2" {
		return s.EdgeMisses
	}
	return s.ObjectGets
}

func trim(v float64) string { return strconv.FormatFloat(v, 'f', -1, 64) }
