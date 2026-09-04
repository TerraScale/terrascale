// Subscribe/execute with checkpoints and typed resets (GOAL D-14/D-38/D-46).
//
// Attach is POST /api/v1/reactive/views; the log URL it returns is polled
// with bounded HTTP long-polling on sync.api.terrascale.tech by default.
// A checkpoint is an opaque authorized-view position. Any identity,
// generation, schema, entitlement, region, or retention mismatch surfaces as
// a typed ResetError with a fresh snapshot; the consumer resubscribes.
// WebSocket is an explicit opt-in transport (not implemented in this stub).
package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// Checkpoint is an opaque authorized-view position (journal cut).
type Checkpoint string

// ResetReason is a typed subscription reset cause.
type ResetReason string

const (
	ResetAuthChanged    ResetReason = "auth_changed"
	ResetSchemaChanged  ResetReason = "schema_changed"
	ResetGeneration     ResetReason = "generation_changed"
	ResetRegionMoved    ResetReason = "region_moved"
	ResetExpired        ResetReason = "checkpoint_expired"
	ResetHistoryExpired ResetReason = "history_expired"
	ResetSlowConsumer   ResetReason = "slow_consumer"
	ResetReconnect      ResetReason = "reconnect"
)

// ResetError is a typed reset: discard local view state and resubscribe.
type ResetError struct {
	Reason     ResetReason `json:"reason"`
	Message    string      `json:"message,omitempty"`
	ViewID     string      `json:"viewId,omitempty"`
	Generation uint64      `json:"generation,omitempty"`
}

func (e *ResetError) Error() string {
	return fmt.Sprintf("sdk: subscription reset (%s): %s", e.Reason, e.Message)
}

// UpdateKind classifies one subscription delivery.
type UpdateKind string

const (
	UpdateSnapshot UpdateKind = "snapshot"
	UpdateInsert   UpdateKind = "insert"
	UpdateUpdate   UpdateKind = "update"
	UpdatePatch    UpdateKind = "patch"
	UpdateRemove   UpdateKind = "remove"
	UpdateMove     UpdateKind = "move"
	UpdateReset    UpdateKind = "reset"
)

// Update is one subscription delivery at a checkpoint.
type Update struct {
	Kind       UpdateKind      `json:"kind"`
	Checkpoint Checkpoint      `json:"checkpoint,omitempty"`
	ID         string          `json:"id,omitempty"`
	Collection string          `json:"collection,omitempty"`
	Body       json.RawMessage `json:"body,omitempty"`
	Reset      *ResetError     `json:"reset,omitempty"`
}

// Transport selects the reactive delivery transport.
type Transport string

const (
	// TransportHTTP is bounded HTTP long-polling (default, CDN-backed).
	TransportHTTP Transport = "http"
	// TransportWebSocket opts into the direct transport.
	TransportWebSocket Transport = "websocket"
)

// SubscriptionOptions tunes Subscribe.
type SubscriptionOptions struct {
	// Transport defaults to HTTP long-polling.
	Transport Transport
	// ResumeFrom resumes a retained position; empty starts fresh.
	ResumeFrom Checkpoint
}

// Snapshot is the result of Execute ("subscribe once").
type Snapshot struct {
	ViewID     string     `json:"viewId"`
	Generation uint64     `json:"generation"`
	Checkpoint Checkpoint `json:"checkpoint"`
	Documents  []Document `json:"documents"`
}

type attachRequest struct {
	DatabaseID string     `json:"databaseId"`
	Statement  string     `json:"statement"`
	Params     []any      `json:"params,omitempty"`
	ResumeFrom Checkpoint `json:"resumeFrom,omitempty"`
	Transport  Transport  `json:"transport,omitempty"`
}

type attachResponse struct {
	ViewID     string     `json:"viewId"`
	Generation uint64     `json:"generation"`
	Checkpoint Checkpoint `json:"checkpoint"`
	Snapshot   []Document `json:"snapshot,omitempty"`
	LogURL     string     `json:"logUrl,omitempty"`
	Capability string     `json:"capability,omitempty"`
}

type pollResponse struct {
	Updates    []Update    `json:"updates,omitempty"`
	Checkpoint Checkpoint  `json:"checkpoint,omitempty"`
	Reset      *ResetError `json:"reset,omitempty"`
}

// Subscription is a live reactive stream. Read Events until it closes.
type Subscription struct {
	c          *Client
	viewID     string
	generation uint64
	logURL     string
	capability string

	mu         sync.RWMutex
	checkpoint Checkpoint

	events    chan Update
	done      chan struct{}
	closeOnce sync.Once
	cancel    context.CancelFunc
	wg        sync.WaitGroup
}

// Events returns the delivery channel, closed when the stream ends.
func (s *Subscription) Events() <-chan Update { return s.events }

// ViewID returns the opaque attached view id.
func (s *Subscription) ViewID() string { return s.viewID }

// Generation returns the attached view generation.
func (s *Subscription) Generation() uint64 { return s.generation }

// Checkpoint returns the latest consumed position.
func (s *Subscription) Checkpoint() Checkpoint {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.checkpoint
}

func (s *Subscription) setCheckpoint(cp Checkpoint) {
	if cp == "" {
		return
	}
	s.mu.Lock()
	s.checkpoint = cp
	s.mu.Unlock()
}

// Close terminates the stream; Events is closed by the poll loop.
func (s *Subscription) Close() {
	s.closeOnce.Do(func() {
		if s.cancel != nil {
			s.cancel()
		}
		close(s.done)
	})
	s.wg.Wait()
}

func (s *Subscription) deliver(u Update) bool {
	select {
	case s.events <- u:
		return true
	default:
		reset := Update{
			Kind:       UpdateReset,
			Checkpoint: s.Checkpoint(),
			Reset: &ResetError{
				Reason:  ResetSlowConsumer,
				Message: "slow consumer: event buffer full",
				ViewID:  s.viewID,
			},
		}
		select {
		case s.events <- reset:
		default:
		}
		return false
	}
}

// Execute attaches to a shared view and returns one snapshot ("subscribe
// once"). Use Subscribe for the live stream.
func (c *Client) Execute(ctx context.Context, databaseID, statement string, params []any, opts ...RequestOption) (*Snapshot, error) {
	attach, err := c.attach(ctx, databaseID, statement, params, SubscriptionOptions{}, opts)
	if err != nil {
		return nil, err
	}
	return &Snapshot{
		ViewID:     attach.ViewID,
		Generation: attach.Generation,
		Checkpoint: attach.Checkpoint,
		Documents:  append([]Document(nil), attach.Snapshot...),
	}, nil
}

// Subscribe attaches to a shared view and streams deltas until ctx ends or
// Close is called. A typed reset is delivered as an UpdateReset event and
// ends the stream; resubscribe for a fresh authorized snapshot.
func (c *Client) Subscribe(ctx context.Context, databaseID, statement string, params []any, sub SubscriptionOptions, opts ...RequestOption) (*Subscription, error) {
	if sub.Transport == TransportWebSocket {
		return nil, fmt.Errorf("sdk: websocket transport is not implemented in this stub; use HTTP long-polling")
	}
	attach, err := c.attach(ctx, databaseID, statement, params, sub, opts)
	if err != nil {
		return nil, err
	}
	if attach.LogURL == "" {
		return nil, fmt.Errorf("sdk: attach response missing log URL")
	}
	pollCtx, cancel := context.WithCancel(context.Background())
	s := &Subscription{
		c:          c,
		viewID:     attach.ViewID,
		generation: attach.Generation,
		logURL:     attach.LogURL,
		capability: attach.Capability,
		checkpoint: attach.Checkpoint,
		events:     make(chan Update, 128),
		done:       make(chan struct{}),
		cancel:     cancel,
	}
	for _, doc := range attach.Snapshot {
		cp := s.Checkpoint()
		if !s.deliver(Update{Kind: UpdateSnapshot, Checkpoint: cp, ID: doc.ID, Collection: doc.Collection, Body: doc.Body}) {
			break
		}
	}
	// Start polling only after the initial snapshot is queued, so a fast
	// origin cannot deliver a delta ahead of the snapshot. Detach the poll
	// loop from the caller's ctx lifetime via Close and the parent ctx.
	s.wg.Add(1)
	go s.pollLoop(pollCtx)
	go func() {
		select {
		case <-ctx.Done():
			s.Close()
		case <-s.done:
		}
	}()
	return s, nil
}

func (c *Client) attach(ctx context.Context, databaseID, statement string, params []any, sub SubscriptionOptions, opts []RequestOption) (*attachResponse, error) {
	if strings.TrimSpace(databaseID) == "" {
		return nil, fmt.Errorf("sdk: database id cannot be empty")
	}
	if !isReadOnlyStatement(statement) {
		return nil, fmt.Errorf("sdk: only live-subset read-only statements are subscribable")
	}
	transport := sub.Transport
	if transport == "" {
		transport = TransportHTTP
	}
	req := attachRequest{
		DatabaseID: databaseID,
		Statement:  statement,
		Params:     params,
		ResumeFrom: sub.ResumeFrom,
		Transport:  transport,
	}
	var out attachResponse
	if err := c.do(ctx, "POST", "/api/v1/reactive/views", nil, req, &out, "reactive.attach", opts...); err != nil {
		return nil, err
	}
	if strings.TrimSpace(out.ViewID) == "" {
		return nil, fmt.Errorf("sdk: attach response missing view id")
	}
	return &out, nil
}

func (s *Subscription) pollLoop(ctx context.Context) {
	defer s.wg.Done()
	defer close(s.events)
	defer s.cancel()
	for {
		select {
		case <-ctx.Done():
			return
		case <-s.done:
			return
		default:
		}
		next, prog, reset, err := s.pollOnce(ctx)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			// Transient poll failure: back off briefly, stay subscribed.
			if !sleepOrDone(ctx, s.done, pollBackoff) {
				return
			}
			continue
		}
		if reset != nil {
			cp := s.Checkpoint()
			s.deliver(Update{Kind: UpdateReset, Checkpoint: cp, Reset: reset})
			return
		}
		if len(next) == 0 && prog == "" {
			if !sleepOrDone(ctx, s.done, pollBackoff) {
				return
			}
			continue
		}
		for _, u := range next {
			if u.Checkpoint != "" {
				s.setCheckpoint(u.Checkpoint)
				u.Checkpoint = s.Checkpoint()
			} else {
				u.Checkpoint = s.Checkpoint()
			}
			if !s.deliver(u) {
				return
			}
		}
		if prog != "" {
			s.setCheckpoint(prog)
		}
	}
}

// pollBackoff spaces empty long-polls so a stub server that answers
// immediately cannot spin the loop.
const pollBackoff = 100 * time.Millisecond

func sleepOrDone(ctx context.Context, done <-chan struct{}, d time.Duration) bool {
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-done:
		return false
	case <-timer.C:
		return true
	}
}

func (s *Subscription) pollOnce(ctx context.Context) ([]Update, Checkpoint, *ResetError, error) {
	checkpoint := s.Checkpoint()
	logURL := s.logURL
	if strings.HasPrefix(logURL, "/") {
		logURL = strings.TrimSuffix(s.c.currentBase(), "/") + logURL
	}
	u, err := url.Parse(logURL)
	if err != nil {
		return nil, "", nil, err
	}
	q := u.Query()
	if checkpoint != "" {
		q.Set("offset", string(checkpoint))
	}
	u.RawQuery = q.Encode()
	req, err := http.NewRequestWithContext(ctx, "GET", u.String(), nil)
	if err != nil {
		return nil, "", nil, err
	}
	req.Header.Set("Accept", "application/json")
	if s.capability != "" {
		req.Header.Set("X-TerraScale-View-Capability", s.capability)
	}
	if token := s.c.token(); token != "" {
		if strings.HasPrefix(token, "Bearer ") {
			req.Header.Set("Authorization", token)
		} else {
			req.Header.Set("Authorization", "Bearer "+token)
		}
	}
	resp, err := s.c.http.Do(req)
	if err != nil {
		return nil, "", nil, err
	}
	defer resp.Body.Close()
	var poll pollResponse
	dec := json.NewDecoder(resp.Body)
	if err := dec.Decode(&poll); err != nil {
		// Empty long-poll timeout carries no body; stay subscribed.
		return nil, "", nil, nil
	}
	if poll.Reset != nil {
		if poll.Reset.ViewID == "" {
			poll.Reset.ViewID = s.viewID
		}
		if poll.Checkpoint != "" {
			s.setCheckpoint(poll.Checkpoint)
		}
		return nil, "", poll.Reset, nil
	}
	return poll.Updates, poll.Checkpoint, nil, nil
}
