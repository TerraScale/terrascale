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
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
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

	// Reset reasons from the terrascale.sync.v2 contract.
	ResetRetentionAdvanced    ResetReason = "retention_advanced"
	ResetCheckpointCurrent    ResetReason = "checkpoint_not_current"
	ResetCheckpointNotCurrent ResetReason = ResetCheckpointCurrent
	ResetViewDiverged         ResetReason = "view_diverged"
	ResetOversized            ResetReason = "oversized"
	ResetInvalidOperations    ResetReason = "invalid_operations"
	ResetInvalidCheckpoint    ResetReason = "invalid_checkpoint"
	ResetBackpressure         ResetReason = "backpressure"
)

// ResetError is a typed reset: discard local view state and resubscribe.
type ResetError struct {
	Reason     ResetReason `json:"reason"`
	Message    string      `json:"message,omitempty"`
	ViewID     string      `json:"viewId,omitempty"`
	Generation uint64      `json:"generation,omitempty"`
}

func (e *ResetError) UnmarshalJSON(data []byte) error {
	type wire struct {
		Reason     ResetReason `json:"reason"`
		Message    string      `json:"message"`
		ViewID     string      `json:"viewId"`
		View       string      `json:"view"`
		Generation uint64      `json:"generation"`
	}
	var v wire
	if err := json.Unmarshal(data, &v); err != nil {
		return err
	}
	e.Reason, e.Message, e.ViewID, e.Generation = v.Reason, v.Message, v.ViewID, v.Generation
	if e.ViewID == "" {
		e.ViewID = v.View
	}
	return nil
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
	ViewID           string     `json:"viewId"`
	Generation       uint64     `json:"generation"`
	Checkpoint       Checkpoint `json:"checkpoint"`
	Snapshot         []Document `json:"snapshot,omitempty"`
	LogURL           string     `json:"logUrl,omitempty"`
	Capability       string     `json:"capability,omitempty"`
	CapabilityHeader string     `json:"capabilityHeader,omitempty"`
	SnapshotChunks   []string   `json:"snapshotChunks,omitempty"`
	Offset           string     `json:"-"`
}

type pollResponse struct {
	Updates    []Update    `json:"updates,omitempty"`
	Checkpoint Checkpoint  `json:"checkpoint,omitempty"`
	Reset      *ResetError `json:"reset,omitempty"`
	Offset     string      `json:"-"`
}

// UnmarshalJSON accepts both the hand-written SDK fixture shape and the
// published reactive attach shape. The latter uses snake_case and keeps the
// numeric log offset separate from the opaque checkpoint.
func (a *attachResponse) UnmarshalJSON(data []byte) error {
	type wire struct {
		ViewID          string          `json:"viewId"`
		View            string          `json:"view"`
		Generation      uint64          `json:"generation"`
		Checkpoint      Checkpoint      `json:"checkpoint"`
		Snapshot        []Document      `json:"snapshot"`
		LogURL          string          `json:"logUrl"`
		Log             string          `json:"log"`
		Capability      string          `json:"capability"`
		CapabilityHead  string          `json:"capability_header"`
		CapabilityCamel string          `json:"capabilityHeader"`
		Chunks          []string        `json:"snapshot_chunks"`
		ChunksCamel     []string        `json:"snapshotChunks"`
		Offset          json.RawMessage `json:"offset"`
	}
	var v wire
	if err := json.Unmarshal(data, &v); err != nil {
		return err
	}
	a.ViewID = v.ViewID
	if a.ViewID == "" {
		a.ViewID = v.View
	}
	a.Generation, a.Checkpoint, a.Snapshot = v.Generation, v.Checkpoint, v.Snapshot
	a.LogURL, a.Capability = v.LogURL, v.Capability
	if a.LogURL == "" {
		a.LogURL = v.Log
	}
	a.CapabilityHeader = v.CapabilityHead
	if a.CapabilityHeader == "" {
		a.CapabilityHeader = v.CapabilityCamel
	}
	a.SnapshotChunks = v.Chunks
	if len(a.SnapshotChunks) == 0 {
		a.SnapshotChunks = v.ChunksCamel
	}
	a.Offset = rawOffset(v.Offset)
	return nil
}

func (p *pollResponse) UnmarshalJSON(data []byte) error {
	type wire struct {
		Updates    []Update        `json:"updates"`
		Checkpoint Checkpoint      `json:"checkpoint"`
		Reset      *ResetError     `json:"reset"`
		Offset     json.RawMessage `json:"offset"`
		Revision   json.RawMessage `json:"revision"`
		Error      json.RawMessage `json:"error"`
	}
	var v wire
	if err := json.Unmarshal(data, &v); err != nil {
		return err
	}
	p.Updates, p.Checkpoint, p.Reset = v.Updates, v.Checkpoint, v.Reset
	p.Offset = rawOffset(v.Offset)
	if p.Offset == "" {
		p.Offset = rawOffset(v.Revision)
	}
	_ = v.Error // Retained for compatibility with {error, reset} responses.
	return nil
}

func rawOffset(raw json.RawMessage) string {
	if len(raw) == 0 || bytes.Equal(raw, []byte("null")) {
		return ""
	}
	var s string
	if json.Unmarshal(raw, &s) == nil {
		return strings.TrimSpace(s)
	}
	var n json.Number
	if json.Unmarshal(raw, &n) == nil {
		return n.String()
	}
	return ""
}

// Subscription is a live reactive stream. Read Events until it closes.
type Subscription struct {
	c                *Client
	viewID           string
	generation       uint64
	logURL           string
	capability       string
	capabilityHeader string

	mu         sync.RWMutex
	checkpoint Checkpoint
	offset     string
	etag       string

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
	s.etag = ""
	s.mu.Unlock()
}

func (s *Subscription) progress() (Checkpoint, string, string) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.checkpoint, s.offset, s.etag
}

func (s *Subscription) setProgress(cp Checkpoint, offset, etag string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	oldCheckpoint := s.checkpoint
	// The original SDK fixture used the opaque checkpoint as its offset. The
	// published contract carries both values, so only apply this compatibility
	// conversion when the existing offset is exactly that old checkpoint.
	if offset == "" && cp != "" && s.offset == string(oldCheckpoint) {
		offset = string(cp)
	}
	if offset != "" && s.offset != "" {
		if old, errOld := strconv.ParseUint(s.offset, 10, 64); errOld == nil {
			if next, errNext := strconv.ParseUint(offset, 10, 64); errNext == nil && next < old {
				return false
			}
		}
	}
	if cp != "" && cp != s.checkpoint {
		s.checkpoint, s.etag = cp, ""
	}
	if offset != "" {
		s.offset = offset
	}
	if etag != "" {
		s.etag = etag
	}
	return true
}

func (s *Subscription) acceptsOffset(offset string) bool {
	if offset == "" {
		return true
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	if s.offset == "" {
		return true
	}
	old, oldErr := strconv.ParseUint(s.offset, 10, 64)
	next, nextErr := strconv.ParseUint(offset, 10, 64)
	return oldErr != nil || nextErr != nil || next >= old
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
		Documents:  cloneDocuments(attach.Snapshot),
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
	if strings.TrimSpace(attach.Capability) == "" {
		return nil, fmt.Errorf("sdk: attach response missing view capability")
	}
	parsedLogURL, err := url.Parse(attach.LogURL)
	if err != nil || (parsedLogURL.IsAbs() && parsedLogURL.Scheme != "http" && parsedLogURL.Scheme != "https") {
		return nil, fmt.Errorf("sdk: attach response has invalid log URL")
	}
	pollCtx, cancel := context.WithCancel(context.Background())
	s := &Subscription{
		c:                c,
		viewID:           attach.ViewID,
		generation:       attach.Generation,
		logURL:           attach.LogURL,
		capability:       attach.Capability,
		capabilityHeader: attach.CapabilityHeader,
		checkpoint:       attach.Checkpoint,
		offset:           attachOffset(attach),
		events:           make(chan Update, 128),
		done:             make(chan struct{}),
		cancel:           cancel,
	}
	for _, doc := range attach.Snapshot {
		cp := s.Checkpoint()
		if !s.deliver(Update{Kind: UpdateSnapshot, Checkpoint: cp, ID: doc.ID, Collection: doc.Collection, Body: cloneRaw(doc.Body)}) {
			return nil, fmt.Errorf("sdk: initial snapshot exceeds subscription buffer")
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

func cloneRaw(raw json.RawMessage) json.RawMessage {
	return append(json.RawMessage(nil), raw...)
}

func cloneDocuments(in []Document) []Document {
	if in == nil {
		return nil
	}
	out := make([]Document, len(in))
	for i, doc := range in {
		out[i] = doc
		out[i].Body = cloneRaw(doc.Body)
	}
	return out
}

func attachOffset(a *attachResponse) string {
	if a.Offset != "" {
		return a.Offset
	}
	u, err := url.Parse(a.LogURL)
	if err == nil {
		if value := u.Query().Get("offset"); value != "" {
			return value
		}
	}
	return string(a.Checkpoint)
}

func (s *Subscription) pollLoop(ctx context.Context) {
	defer s.wg.Done()
	defer close(s.events)
	defer s.cancel()
	failures := 0
	for {
		select {
		case <-ctx.Done():
			return
		case <-s.done:
			return
		default:
		}
		next, prog, offset, etag, reset, err := s.pollOnce(ctx)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			failures++
			if failures > pollRetryLimit {
				reset := &ResetError{Reason: ResetReconnect, Message: "reactive log unavailable after bounded retries", ViewID: s.viewID, Generation: s.generation}
				s.deliver(Update{Kind: UpdateReset, Checkpoint: s.Checkpoint(), Reset: reset})
				return
			}
			if !sleepOrDone(ctx, s.done, pollRetryDelay(failures)) {
				return
			}
			continue
		}
		failures = 0
		if reset != nil {
			cp := s.Checkpoint()
			s.deliver(Update{Kind: UpdateReset, Checkpoint: cp, Reset: reset})
			return
		}
		if !s.acceptsOffset(offset) {
			reset := &ResetError{Reason: ResetReconnect, Message: "reactive log returned a stale offset", ViewID: s.viewID, Generation: s.generation}
			s.deliver(Update{Kind: UpdateReset, Checkpoint: s.Checkpoint(), Reset: reset})
			return
		}
		if len(next) == 0 && prog == "" && offset == "" {
			if etag != "" {
				s.setProgress("", "", etag)
			}
			if !sleepOrDone(ctx, s.done, pollBackoff) {
				return
			}
			continue
		}
		for _, u := range next {
			if u.Checkpoint == "" {
				u.Checkpoint = prog
			}
			u.Body = cloneRaw(u.Body)
			if !s.deliver(u) {
				return
			}
			if u.Checkpoint != "" {
				s.setProgress(u.Checkpoint, "", "")
			}
		}
		if prog != "" || offset != "" || etag != "" {
			s.setProgress(prog, offset, etag)
		}
	}
}

// pollBackoff spaces empty long-polls so a stub server that answers
// immediately cannot spin the loop.
const pollBackoff = 100 * time.Millisecond

const (
	pollRetryLimit          = 5
	maxReactiveBody         = 8 << 20
	defaultCapabilityHeader = "X-TerraScale-Capability"
	legacyCapabilityHeader  = "X-TerraScale-View-Capability"
)

func pollRetryDelay(attempt int) time.Duration {
	if attempt < 1 {
		attempt = 1
	}
	d := pollBackoff << (attempt - 1)
	if d > 2*time.Second {
		return 2 * time.Second
	}
	return d
}

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

func (s *Subscription) pollOnce(ctx context.Context) ([]Update, Checkpoint, string, string, *ResetError, error) {
	checkpoint, offset, etag := s.progress()
	logURL := s.logURL
	if strings.HasPrefix(logURL, "/") {
		base := s.c.APIBaseURL()
		if base == DefaultAPIBaseURL {
			base = DefaultSyncBaseURL
		}
		logURL = strings.TrimSuffix(base, "/") + logURL
	}
	u, err := url.Parse(logURL)
	if err != nil {
		return nil, "", "", "", nil, err
	}
	q := url.Values{}
	if offset != "" {
		q.Set("offset", offset)
	} else if value := u.Query().Get("offset"); value != "" {
		q.Set("offset", value)
	} else if checkpoint != "" {
		// Legacy fixtures use the opaque checkpoint as their log offset.
		q.Set("offset", string(checkpoint))
	}
	u.RawQuery = q.Encode()
	req, err := http.NewRequestWithContext(ctx, "GET", u.String(), nil)
	if err != nil {
		return nil, "", "", "", nil, err
	}
	req.Header.Set("Accept", "application/msgpack, application/json")
	if s.capability != "" {
		header := s.capabilityHeader
		if header == "" {
			header = defaultCapabilityHeader
		}
		req.Header.Set(header, s.capability)
		// Older pre-contract fixtures used this name. Keep it only when the
		// attach response did not identify the published header.
		if s.capabilityHeader == "" {
			req.Header.Set(legacyCapabilityHeader, s.capability)
		}
	}
	if etag != "" {
		req.Header.Set("If-None-Match", etag)
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
		return nil, "", "", "", nil, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, maxReactiveBody+1))
	if err != nil {
		return nil, "", "", "", nil, err
	}
	if len(body) > maxReactiveBody {
		return nil, "", "", "", nil, fmt.Errorf("sdk: reactive response exceeds %d bytes", maxReactiveBody)
	}
	if resp.StatusCode == http.StatusNotModified {
		return nil, "", "", etag, nil, nil
	}
	var poll pollResponse
	if len(bytes.TrimSpace(body)) == 0 && resp.StatusCode < 400 {
		// Empty long-poll timeout carries no body; stay subscribed.
		return nil, "", "", "", nil, nil
	}
	if err := json.Unmarshal(body, &poll); err != nil {
		if resp.StatusCode >= 400 {
			return nil, "", "", "", nil, &APIError{Method: http.MethodGet, Path: u.Path, Status: resp.StatusCode, Message: string(body)}
		}
		return nil, "", "", "", nil, err
	}
	if poll.Reset != nil {
		if poll.Reset.ViewID == "" {
			poll.Reset.ViewID = s.viewID
		}
		if poll.Checkpoint != "" || poll.Offset != "" {
			s.setProgress(poll.Checkpoint, poll.Offset, "")
		}
		return nil, "", "", "", normalizeReset(poll.Reset), nil
	}
	if resp.StatusCode >= 400 {
		if reset := resetForStatus(resp.StatusCode); reset != nil {
			reset.ViewID, reset.Generation = s.viewID, s.generation
			return nil, "", "", "", reset, nil
		}
		return nil, "", "", "", nil, &APIError{Method: http.MethodGet, Path: u.Path, Status: resp.StatusCode, Message: string(body)}
	}
	responseETag := resp.Header.Get("ETag")
	if cacheNoStore(resp.Header.Get("Cache-Control")) || cacheNoStore(resp.Header.Get("Cloudflare-CDN-Cache-Control")) {
		responseETag = ""
	}
	return poll.Updates, poll.Checkpoint, poll.Offset, responseETag, nil, nil
}

const DefaultSyncBaseURL = "https://sync.api.terrascale.tech"

func cacheNoStore(value string) bool {
	for _, directive := range strings.Split(strings.ToLower(value), ",") {
		if strings.TrimSpace(directive) == "no-store" {
			return true
		}
	}
	return false
}

func normalizeReset(reset *ResetError) *ResetError {
	if reset == nil {
		return nil
	}
	switch strings.ToLower(strings.TrimSpace(string(reset.Reason))) {
	case "retention":
		reset.Reason = ResetHistoryExpired
	case "retention_advanced":
		reset.Reason = ResetRetentionAdvanced
	case "checkpoint_not_current":
		reset.Reason = ResetCheckpointCurrent
	}
	return reset
}

func resetForStatus(status int) *ResetError {
	switch status {
	case http.StatusUnauthorized, http.StatusForbidden:
		return &ResetError{Reason: ResetAuthChanged, Message: "view capability is no longer authorized"}
	case http.StatusNotFound:
		return &ResetError{Reason: ResetGeneration, Message: "view generation is no longer available"}
	case http.StatusConflict:
		return &ResetError{Reason: ResetCheckpointCurrent, Message: "log offset is no longer current"}
	case http.StatusGone:
		return &ResetError{Reason: ResetHistoryExpired, Message: "log history is no longer retained"}
	default:
		return nil
	}
}
