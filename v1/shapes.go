package v1

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"iter"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// ShapeRequest defines a shape: a filtered, projected view of a collection.
type ShapeRequest struct {
	Collection string   `json:"collection"`
	Filter     Filter   `json:"filter,omitzero"`
	Project    []string `json:"project,omitempty"`
}

// ShapeInfo is the gatekeeper's answer for a shape.
type ShapeInfo struct {
	ShapeID string `json:"shape_id"`
	Handle  string `json:"handle"`
	Offset  string `json:"offset"`
	// URL is the edge URL of the shape's first request.
	URL string `json:"url"`
	// Token is the shape token for the edge; it expires at ExpiresAt.
	Token     string    `json:"token"`
	ExpiresAt Timestamp `json:"expires_at"`
}

// CreateShape calls the gatekeeper (needs the shape permission). Call it again
// before ExpiresAt to obtain a fresh edge token.
func (d *Database) CreateShape(ctx context.Context, req ShapeRequest) (*ShapeInfo, error) {
	if err := checkName("database", d.name); err != nil {
		return nil, err
	}
	if err := checkName("collection", req.Collection); err != nil {
		return nil, err
	}
	var info ShapeInfo
	if _, err := d.c.call(ctx, request{method: http.MethodPost, path: []string{"v1", "databases", d.name, "shapes"}}, req, &info); err != nil {
		return nil, err
	}
	if !shapeIDPattern.MatchString(info.ShapeID) {
		return nil, fmt.Errorf("terrabase: gatekeeper returned invalid shape_id %q", info.ShapeID)
	}
	return &info, nil
}

// ShapeEventType classifies a ShapeEvent.
type ShapeEventType string

// Shape event types.
const (
	// EventChanges carries snapshot or log changes to apply in order.
	EventChanges ShapeEventType = "changes"
	// EventUpToDate reports that the subscription caught up with the head.
	EventUpToDate ShapeEventType = "up-to-date"
	// EventReset means the server asked for a refetch (must_refetch): discard
	// all shape state; a fresh snapshot follows.
	EventReset ShapeEventType = "reset"
)

// ShapeEvent is one step of a shape subscription.
type ShapeEvent struct {
	Type   ShapeEventType
	Handle string
	// Offset is the position the response started at ("snap.S.P" or a log
	// offset); NextOffset is where the next request resumes.
	Offset     string
	NextOffset string
	// Snapshot is true for snapshot pages.
	Snapshot bool
	UpToDate bool
	Changes  []ShapeChange
}

// ShapeChange is one commit's changes to the shape.
type ShapeChange struct {
	Offset Uint64    `json:"offset"`
	Ops    []ShapeOp `json:"ops"`
}

// ShapeOpKind is upsert or delete.
type ShapeOpKind string

// Shape op kinds.
const (
	OpUpsert ShapeOpKind = "upsert"
	OpDelete ShapeOpKind = "delete"
)

// ShapeOp is one document change. Upserts carry a projected Document, or
// State "conflict" with Siblings; deletes carry only ID.
type ShapeOp struct {
	Op       ShapeOpKind     `json:"op"`
	ID       string          `json:"id"`
	Version  string          `json:"version,omitempty"`
	State    State           `json:"state,omitempty"`
	Document json.RawMessage `json:"document,omitempty"`
	Siblings []Sibling       `json:"siblings,omitempty"`
}

// Decode decodes an upsert's document into v, keeping numbers as json.Number.
func (o *ShapeOp) Decode(v any) error { return Decode(o.Document, v) }

type shapeBody struct {
	ShapeID    string        `json:"shape_id"`
	Handle     string        `json:"handle"`
	Offset     string        `json:"offset"`
	NextOffset string        `json:"next_offset"`
	UpToDate   bool          `json:"up_to_date"`
	Changes    []ShapeChange `json:"changes"`
}

// SubscribeOption configures Subscribe.
type SubscribeOption func(*subscribeConfig)

type subscribeConfig struct {
	edge          bool
	sse           bool
	resumeHandle  string
	resumeOffset  string
	refreshMargin time.Duration
}

// ViaEdge reads the shape through the edge URL with the shape token,
// refreshing the token through the gatekeeper before it expires.
func ViaEdge() SubscribeOption { return func(c *subscribeConfig) { c.edge = true } }

// WithSSE reads the shape from the origin's server-sent events stream
// instead of long-polling. It cannot be combined with ViaEdge.
func WithSSE() SubscribeOption { return func(c *subscribeConfig) { c.sse = true } }

// ResumeAt continues a previous subscription from a saved handle and offset
// (a ShapeEvent's Handle and NextOffset).
func ResumeAt(handle, offset string) SubscribeOption {
	return func(c *subscribeConfig) { c.resumeHandle, c.resumeOffset = handle, offset }
}

// Subscription follows a shape's log. Next and Events must be used from one
// goroutine; WaitFor, Position, Info and Close are safe from any goroutine.
type Subscription struct {
	db  *Database
	req ShapeRequest
	cfg subscribeConfig

	life      context.Context
	closeLife context.CancelFunc

	// Consumer state, owned by the goroutine calling Next.
	handle, offset, cursor string
	live, upToDate         bool
	obtained               time.Time
	stream                 *sseStream
	pending                *Uint64
	rejected               bool // the edge rejected a freshly refreshed token

	mu       sync.Mutex
	info     ShapeInfo
	progress Uint64
	advanced chan struct{}
	closed   bool
}

// Subscribe creates the shape through the gatekeeper and returns a
// subscription positioned at the start of the snapshot (or at ResumeAt).
func (d *Database) Subscribe(ctx context.Context, req ShapeRequest, opts ...SubscribeOption) (*Subscription, error) {
	var cfg subscribeConfig
	for _, o := range opts {
		o(&cfg)
	}
	if cfg.edge && cfg.sse {
		return nil, errors.New("terrabase: SSE is served by the origin only; do not combine WithSSE and ViaEdge")
	}
	info, err := d.CreateShape(ctx, req)
	if err != nil {
		return nil, err
	}
	life, cancel := context.WithCancel(context.Background())
	s := &Subscription{db: d, req: req, cfg: cfg, life: life, closeLife: cancel,
		info: *info, obtained: time.Now(), handle: info.Handle, offset: info.Offset, advanced: make(chan struct{})}
	if cfg.resumeOffset != "" {
		s.handle, s.offset = cfg.resumeHandle, cfg.resumeOffset
		s.live = isLogOffset(s.offset)
	}
	return s, nil
}

// Info returns the current gatekeeper answer (refreshed for the edge).
func (s *Subscription) Info() ShapeInfo {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.info
}

// Position returns the highest log offset whose changes the consumer has
// finished handling.
func (s *Subscription) Position() Uint64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.progress
}

// Close stops the subscription and cancels any request in flight.
func (s *Subscription) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.closed {
		s.closed = true
		s.closeLife()
		close(s.advanced)
	}
	return nil
}

// WaitFor blocks until the consumer has handled the shape up to offset (for
// example a WriteResult.Offset): an event whose NextOffset is at least offset
// was returned by Next and Next was called again, or the log advanced past
// offset without changes for this shape. Someone must be consuming events.
func (s *Subscription) WaitFor(ctx context.Context, offset Uint64) error {
	for {
		s.mu.Lock()
		reached, closed, ch := s.progress >= offset, s.closed, s.advanced
		s.mu.Unlock()
		switch {
		case reached:
			return nil
		case closed:
			return ErrSubscriptionClosed
		}
		select {
		case <-ch:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
}

func (s *Subscription) advance(next string) {
	n, err := parseCanonicalU64(next)
	if err != nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed || Uint64(n) <= s.progress {
		return
	}
	s.progress = Uint64(n)
	close(s.advanced)
	s.advanced = make(chan struct{})
}

// Events iterates over Next until ctx ends, the subscription closes or an
// error is yielded.
func (s *Subscription) Events(ctx context.Context) iter.Seq2[ShapeEvent, error] {
	return func(yield func(ShapeEvent, error) bool) {
		for {
			ev, err := s.Next(ctx)
			if !yield(ev, err) || err != nil {
				return
			}
		}
	}
}

// Next returns the next event. It follows the shape protocol: offset -1
// redirects to the snapshot, snapshot pages, log chunks, then live long-polls
// echoing TB-Shape-Cursor. A must_refetch returns EventReset and restarts from
// the snapshot. Transient failures (network, 429, 502-504) are retried with
// backoff up to the client's MaxRetries in a row.
func (s *Subscription) Next(ctx context.Context) (ShapeEvent, error) {
	if s.pending != nil {
		s.advance(s.pending.String())
		s.pending = nil
	}
	failures := 0
	for {
		s.mu.Lock()
		closed := s.closed
		s.mu.Unlock()
		if closed {
			return ShapeEvent{}, ErrSubscriptionClosed
		}
		var (
			ev   ShapeEvent
			emit bool
			err  error
		)
		if s.cfg.sse {
			ev, emit, err = s.stepSSE(ctx)
		} else {
			ev, emit, err = s.stepPoll(ctx)
		}
		if err != nil {
			if ctx.Err() != nil {
				return ShapeEvent{}, ctx.Err()
			}
			if s.life.Err() != nil {
				return ShapeEvent{}, ErrSubscriptionClosed
			}
			delay, transient := transientDelay(err, failures)
			if !transient || failures >= s.db.c.maxRetries {
				return ShapeEvent{}, err
			}
			failures++
			if err := sleep(ctx, delay); err != nil {
				return ShapeEvent{}, err
			}
			continue
		}
		failures = 0
		if emit {
			if n, err := parseCanonicalU64(ev.NextOffset); err == nil && ev.Type != EventReset {
				u := Uint64(n)
				s.pending = &u
			}
			return ev, nil
		}
		s.advance(s.offset)
	}
}

// transientDelay classifies shape read failures: transport errors, 429,
// the transient 503s and the edge's 502/504 are retried; stored_corruption
// and every other status are returned.
func transientDelay(err error, attempt int) (time.Duration, bool) {
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		return backoff(attempt), true
	}
	switch {
	case apiErr.Status == http.StatusTooManyRequests, isTransient503(apiErr),
		apiErr.Status == http.StatusBadGateway, apiErr.Status == http.StatusGatewayTimeout:
	default:
		return 0, false
	}
	if apiErr.retryAfterSet {
		return min(apiErr.RetryAfter, MaxRetryWait), true
	}
	return backoff(attempt), true
}

func isLogOffset(o string) bool {
	_, err := parseCanonicalU64(o)
	return err == nil
}

// reset discards the position after must_refetch.
func (s *Subscription) reset(handle string) ShapeEvent {
	s.handle, s.offset, s.cursor, s.live, s.upToDate = handle, "-1", "", false, false
	s.closeStream()
	return ShapeEvent{Type: EventReset, Handle: handle, NextOffset: "-1"}
}

// apply consumes one response body and decides whether to emit an event.
func (s *Subscription) apply(b shapeBody) (ShapeEvent, bool, error) {
	if b.ShapeID != s.Info().ShapeID || b.NextOffset == "" {
		return ShapeEvent{}, false, fmt.Errorf("terrabase: malformed shape body (shape %q, next_offset %q)", b.ShapeID, b.NextOffset)
	}
	if b.Handle != "" {
		s.handle = b.Handle
	}
	s.offset = b.NextOffset
	s.live = b.UpToDate && isLogOffset(b.NextOffset)
	ev := ShapeEvent{Type: EventChanges, Handle: s.handle, Offset: b.Offset, NextOffset: b.NextOffset,
		Snapshot: strings.HasPrefix(b.Offset, "snap."), UpToDate: b.UpToDate, Changes: b.Changes}
	wasUpToDate := s.upToDate
	s.upToDate = b.UpToDate
	switch {
	case len(b.Changes) > 0:
		return ev, true, nil
	case b.UpToDate && !wasUpToDate:
		ev.Type = EventUpToDate
		return ev, true, nil
	}
	return ShapeEvent{}, false, nil
}

// refresh calls the gatekeeper again when the edge token is close to expiry
// (or when forced). A changed shape ID means a new lineage, so it resets.
func (s *Subscription) refresh(ctx context.Context, force bool) (*ShapeEvent, error) {
	info := s.Info()
	expires := info.ExpiresAt.Time()
	margin := min(time.Minute, expires.Sub(s.obtained)/2)
	if !force && time.Now().Add(margin).Before(expires) {
		return nil, nil
	}
	fresh, err := s.db.CreateShape(ctx, s.req)
	if err != nil {
		return nil, err
	}
	s.mu.Lock()
	s.info = *fresh
	s.mu.Unlock()
	s.obtained = time.Now()
	if fresh.ShapeID != info.ShapeID {
		ev := s.reset(fresh.Handle)
		return &ev, nil
	}
	return nil, nil
}

func (s *Subscription) shapeURL(suffix string, live bool) (string, string) {
	q := make([]string, 0, 4)
	if s.handle != "" && s.offset != "-1" {
		q = append(q, "handle="+url.QueryEscape(s.handle))
	}
	q = append(q, "offset="+url.QueryEscape(s.offset))
	if live {
		q = append(q, "live=true")
		if s.cursor != "" {
			q = append(q, "cursor="+url.QueryEscape(s.cursor))
		}
	}
	info := s.Info()
	c := s.db.c
	if s.cfg.edge {
		edge, _ := url.Parse(info.URL)
		u := url.URL{Scheme: edge.Scheme, Host: edge.Host, Path: "/v1/shapes/" + info.ShapeID + suffix, RawQuery: strings.Join(q, "&")}
		return u.String(), info.Token
	}
	u := c.base.JoinPath("v1", "shapes", info.ShapeID+suffix)
	u.RawQuery = strings.Join(q, "&")
	return u.String(), c.token
}

// follow307 takes handle and offset from a snapshot redirect's Location.
func (s *Subscription) follow307(requestURL string, h http.Header) error {
	base, _ := url.Parse(requestURL)
	loc, err := base.Parse(h.Get("Location"))
	if err != nil {
		return fmt.Errorf("terrabase: bad shape redirect: %w", err)
	}
	q := loc.Query()
	if q.Get("offset") == "" || q.Get("handle") == "" {
		return fmt.Errorf("terrabase: shape redirect %q lacks handle or offset", loc)
	}
	s.handle, s.offset, s.live = q.Get("handle"), q.Get("offset"), false
	return nil
}

func (s *Subscription) stepPoll(ctx context.Context) (ShapeEvent, bool, error) {
	if s.cfg.edge {
		if ev, err := s.refresh(ctx, false); err != nil || ev != nil {
			return derefEvent(ev), ev != nil, err
		}
	}
	target, token := s.shapeURL("", s.live)
	rctx, cancel := context.WithCancel(ctx)
	defer cancel()
	defer context.AfterFunc(s.life, cancel)()
	resp, err := s.db.c.send(rctx, http.MethodGet, target, token, nil, "", nil, "")
	if err != nil {
		return ShapeEvent{}, false, err
	}
	switch resp.status {
	case http.StatusOK:
		var b shapeBody
		if err := decodeExact(resp.body, &b); err != nil {
			return ShapeEvent{}, false, fmt.Errorf("terrabase: decode shape body: %w", err)
		}
		if b.Handle == "" {
			b.Handle = resp.header.Get("TB-Shape-Handle")
		}
		s.rejected = false
		ev, emit, err := s.apply(b)
		if err == nil && s.live {
			if c := resp.header.Get("TB-Shape-Cursor"); c != "" {
				s.cursor = c
			}
		}
		return ev, emit, err
	case http.StatusTemporaryRedirect:
		return ShapeEvent{}, false, s.follow307(target, resp.header)
	}
	apiErr := parseAPIError(resp.status, resp.header, resp.body)
	switch {
	case apiErr.Code == ErrMustRefetch:
		return s.reset(apiErr.Handle), true, nil
	case s.cfg.edge && resp.status == http.StatusUnauthorized && !s.rejected:
		s.rejected = true
		ev, err := s.refresh(ctx, true)
		if err != nil {
			return ShapeEvent{}, false, err
		}
		return derefEvent(ev), ev != nil, nil
	}
	return ShapeEvent{}, false, apiErr
}

func derefEvent(ev *ShapeEvent) ShapeEvent {
	if ev == nil {
		return ShapeEvent{}
	}
	return *ev
}

func (s *Subscription) closeStream() {
	if s.stream != nil {
		s.stream.cancel()
		s.stream.body.Close()
		s.stream = nil
	}
}

func (s *Subscription) stepSSE(ctx context.Context) (ShapeEvent, bool, error) {
	if s.stream == nil {
		target, token := s.shapeURL("/sse", false)
		sctx, scancel := context.WithCancel(s.life)
		hr, err := http.NewRequestWithContext(sctx, http.MethodGet, target, nil)
		if err != nil {
			scancel()
			return ShapeEvent{}, false, err
		}
		hr.Header.Set("Authorization", "Bearer "+token)
		hr.Header.Set("Accept", "text/event-stream")
		if s.offset != "-1" {
			hr.Header.Set("Last-Event-ID", s.offset)
		}
		stop := context.AfterFunc(ctx, scancel)
		res, err := s.db.c.hc.Do(hr)
		stop()
		if err != nil {
			scancel()
			return ShapeEvent{}, false, err
		}
		if res.StatusCode != http.StatusOK {
			body, _ := io.ReadAll(io.LimitReader(res.Body, s.db.c.maxResponse))
			res.Body.Close()
			scancel()
			apiErr := parseAPIError(res.StatusCode, res.Header, body)
			if apiErr.Code == ErrMustRefetch {
				return s.reset(apiErr.Handle), true, nil
			}
			return ShapeEvent{}, false, apiErr
		}
		s.stream = &sseStream{cancel: scancel, body: res.Body, r: bufio.NewReader(res.Body), max: s.db.c.maxResponse}
	}
	stream := s.stream
	stop := context.AfterFunc(ctx, stream.cancel)
	name, data, err := stream.next()
	stop()
	if err != nil {
		// The next call reconnects with Last-Event-ID after a backoff.
		s.closeStream()
		return ShapeEvent{}, false, fmt.Errorf("terrabase: shape event stream: %w", err)
	}
	switch name {
	case "changes":
		var b shapeBody
		if err := decodeExact(data, &b); err != nil {
			return ShapeEvent{}, false, fmt.Errorf("terrabase: decode shape event: %w", err)
		}
		return s.apply(b)
	case "up-to-date":
		var b struct {
			NextOffset string `json:"next_offset"`
		}
		if err := decodeExact(data, &b); err != nil || b.NextOffset == "" {
			return ShapeEvent{}, false, fmt.Errorf("terrabase: malformed up-to-date event %q", data)
		}
		s.offset = b.NextOffset
		wasUpToDate := s.upToDate
		s.upToDate = true
		return ShapeEvent{Type: EventUpToDate, Handle: s.handle, Offset: b.NextOffset, NextOffset: b.NextOffset, UpToDate: true}, !wasUpToDate, nil
	case "must-refetch":
		var b struct {
			Handle string `json:"handle"`
		}
		if err := decodeExact(data, &b); err != nil {
			return ShapeEvent{}, false, fmt.Errorf("terrabase: malformed must-refetch event %q", data)
		}
		return s.reset(b.Handle), true, nil
	}
	return ShapeEvent{}, false, nil
}

// sseStream reads text/event-stream events.
type sseStream struct {
	cancel context.CancelFunc
	body   io.ReadCloser
	r      *bufio.Reader
	max    int64
}

// next returns the next dispatched event's name and data. Comment lines and
// events without data are skipped.
func (st *sseStream) next() (string, []byte, error) {
	var (
		name string
		data bytes.Buffer
		has  bool
	)
	for {
		line, err := st.readLine()
		if err != nil {
			return "", nil, err
		}
		if line == "" {
			if has {
				return cmpOr(name, "message"), data.Bytes(), nil
			}
			name = ""
			continue
		}
		if strings.HasPrefix(line, ":") {
			continue
		}
		field, value, _ := strings.Cut(line, ":")
		value = strings.TrimPrefix(value, " ")
		switch field {
		case "event":
			name = value
		case "data":
			if has {
				data.WriteByte('\n')
			}
			data.WriteString(value)
			has = true
			if int64(data.Len()) > st.max {
				return "", nil, fmt.Errorf("terrabase: SSE event exceeds %d bytes", st.max)
			}
		}
	}
}

func (st *sseStream) readLine() (string, error) {
	var b []byte
	for {
		chunk, isPrefix, err := st.r.ReadLine()
		if err != nil {
			return "", err
		}
		b = append(b, chunk...)
		if int64(len(b)) > st.max {
			return "", fmt.Errorf("terrabase: SSE line exceeds %d bytes", st.max)
		}
		if !isPrefix {
			return string(b), nil
		}
	}
}
