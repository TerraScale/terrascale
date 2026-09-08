// Subscribe/execute with checkpoints and typed resets (GOAL D-14/D-38/D-46).
//
// Attach is POST /api/v1/reactive/views; the log URL it returns is polled
// with bounded HTTP long-polling on sync.api.terrascale.tech by default.
// A checkpoint is an opaque authorized-view position. Any identity,
// generation, schema, entitlement, region, or retention mismatch surfaces as
// a typed ResetError with a fresh snapshot; the consumer resubscribes.
// WebSocket is an explicit opt-in transport.
package sdk

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/coder/websocket"
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

	// Reset reasons from the terrascale.sync.v1 contract.
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
	Reason                  ResetReason `json:"reason"`
	Message                 string      `json:"message,omitempty"`
	ViewID                  string      `json:"viewId,omitempty"`
	Generation              uint64      `json:"generation,omitempty"`
	ViewGeneration          uint64      `json:"view_generation,omitempty"`
	AuthorizationGeneration uint64      `json:"authorization_generation,omitempty"`
	Revision                uint64      `json:"revision,omitempty"`
	Digest                  string      `json:"digest,omitempty"`
	Removed                 []string    `json:"removed,omitempty"`
	Snapshot                []Document  `json:"snapshot,omitempty"`
	CheckpointBytes         []byte      `json:"-"`
}

func (e *ResetError) UnmarshalJSON(data []byte) error {
	type wire struct {
		Reason                  ResetReason `json:"reason"`
		Message                 string      `json:"message"`
		ViewID                  string      `json:"viewId"`
		View                    string      `json:"view"`
		Generation              uint64      `json:"generation"`
		ViewGeneration          uint64      `json:"view_generation"`
		AuthorizationGeneration uint64      `json:"authorization_generation"`
		Revision                uint64      `json:"revision"`
		Digest                  string      `json:"digest"`
	}
	var v wire
	if err := json.Unmarshal(data, &v); err != nil {
		return err
	}
	e.Reason, e.Message, e.ViewID, e.Generation = v.Reason, v.Message, v.ViewID, v.Generation
	e.ViewGeneration, e.AuthorizationGeneration, e.Revision, e.Digest = v.ViewGeneration, v.AuthorizationGeneration, v.Revision, v.Digest
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
	Kind       UpdateKind `json:"kind"`
	Checkpoint Checkpoint `json:"checkpoint,omitempty"`
	ID         string     `json:"id,omitempty"`
	// Position is the canonical zero-based result position for insert/move
	// operations. It is omitted for operation kinds that do not carry one.
	Position uint32 `json:"position,omitempty"`
	// View and authorization generations, revision bounds, and digest are the
	// atomic state metadata carried by every server result frame.
	ViewGeneration          uint64          `json:"view_generation,omitempty"`
	AuthorizationGeneration uint64          `json:"authorization_generation,omitempty"`
	Revision                uint64          `json:"revision,omitempty"`
	FromRevision            uint64          `json:"from_revision,omitempty"`
	ToRevision              uint64          `json:"to_revision,omitempty"`
	Digest                  string          `json:"digest,omitempty"`
	Collection              string          `json:"collection,omitempty"`
	Body                    json.RawMessage `json:"body,omitempty"`
	// Fields retains every projected field from the indexed wire item. Body is
	// populated as a convenience when the projection contains the body path.
	Fields          map[string]json.RawMessage `json:"fields,omitempty"`
	CheckpointBytes []byte                     `json:"-"`
	// Snapshot is populated on a replacement reset. Consumers should discard
	// their old local view and install these documents atomically.
	Snapshot []Document  `json:"snapshot,omitempty"`
	Reset    *ResetError `json:"reset,omitempty"`
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
	// RegionHint requests a canonical launch region for the attach.
	RegionHint RegionCode
}

// Snapshot is the result of Execute ("subscribe once").
type Snapshot struct {
	ViewID     string     `json:"viewId"`
	Generation uint64     `json:"generation"`
	Checkpoint Checkpoint `json:"checkpoint"`
	Documents  []Document `json:"documents"`
}

type attachRequest struct {
	DatabaseID string `json:"databaseId"`
	Statement  string `json:"statement"`
	// Parameters is base64-encoded canonical JSON object bytes. The producer
	// decodes this object into named SQL bindings before materializing a view.
	Parameters string      `json:"parameters"`
	Checkpoint *Checkpoint `json:"checkpoint"`
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
	Region           string     `json:"region,omitempty"`
	Offset           string     `json:"-"`
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
		LogURL          string          `json:"logUrl"`
		Log             string          `json:"log"`
		Capability      string          `json:"capability"`
		CapabilityHead  string          `json:"capability_header"`
		CapabilityCamel string          `json:"capabilityHeader"`
		Chunks          []string        `json:"snapshot_chunks"`
		ChunksCamel     []string        `json:"snapshotChunks"`
		Region          string          `json:"region"`
		SnapshotRaw     json.RawMessage `json:"snapshot"`
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
	a.Generation, a.Checkpoint, a.Snapshot, a.Region = v.Generation, decodeCheckpoint(v.Checkpoint), nil, v.Region
	if len(v.SnapshotRaw) != 0 && !bytes.Equal(v.SnapshotRaw, []byte("null")) {
		var rawDocs []struct {
			ID         string          `json:"id"`
			Collection string          `json:"collection"`
			Body       json.RawMessage `json:"body"`
		}
		if err := json.Unmarshal(v.SnapshotRaw, &rawDocs); err != nil {
			var inline struct {
				ViewGeneration      uint64 `json:"view_generation"`
				ViewGenerationCamel uint64 `json:"viewGeneration"`
				Revision            uint64 `json:"revision"`
				Items               []struct {
					Identity string `json:"identity"`
					Fields   []struct {
						Path  string          `json:"path"`
						Value json.RawMessage `json:"value"`
					} `json:"fields"`
				} `json:"items"`
				Checkpoint string `json:"checkpoint"`
			}
			if errInline := json.Unmarshal(v.SnapshotRaw, &inline); errInline != nil {
				return fmt.Errorf("sdk: invalid inline reactive snapshot: %w", err)
			}
			for _, item := range inline.Items {
				doc := Document{ID: item.Identity}
				for _, field := range item.Fields {
					if field.Path != "body" {
						continue
					}
					var encoded string
					if json.Unmarshal(field.Value, &encoded) == nil {
						if decoded, errDecode := base64.StdEncoding.DecodeString(encoded); errDecode == nil {
							doc.Body = cloneRaw(decoded)
							continue
						}
					}
					doc.Body = cloneRaw(field.Value)
				}
				a.Snapshot = append(a.Snapshot, doc)
			}
			if a.Checkpoint == "" && inline.Checkpoint != "" {
				a.Checkpoint = decodeCheckpoint(Checkpoint(inline.Checkpoint))
			}
		} else {
			for _, item := range rawDocs {
				a.Snapshot = append(a.Snapshot, Document{ID: item.ID, Collection: item.Collection, Body: cloneRaw(item.Body)})
			}
		}
	}
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
	region           string
	sessionToken     string
	statement        string
	parameters       []byte
	websocket        *websocket.Conn

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
		if s.websocket != nil {
			_ = s.websocket.Close(websocket.StatusNormalClosure, "closed")
		}
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
	if err := validateCapability(attach.Capability); err != nil {
		return nil, err
	}
	attach.CapabilityHeader, err = normalizeCapabilityHeader(attach.CapabilityHeader)
	if err != nil {
		return nil, err
	}
	if len(attach.Snapshot) == 0 && len(attach.SnapshotChunks) > 0 {
		attach.Snapshot, attach.Checkpoint, err = c.fetchSnapshotChunks(ctx, attach)
		if err != nil {
			return nil, err
		}
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
	capabilityHeader, err := normalizeCapabilityHeader(attach.CapabilityHeader)
	if err != nil {
		return nil, err
	}
	if err := validateCapability(attach.Capability); err != nil {
		return nil, err
	}
	attach.LogURL, err = canonicalLogURL(c, attach.LogURL, attach.ViewID, attach.Generation)
	if err != nil {
		return nil, err
	}
	attach.CapabilityHeader = capabilityHeader
	if len(attach.Snapshot) == 0 && len(attach.SnapshotChunks) > 0 {
		attach.Snapshot, attach.Checkpoint, err = c.fetchSnapshotChunks(ctx, attach)
		if err != nil {
			return nil, err
		}
	}
	pollCtx, cancel := context.WithCancel(ctx)
	s := &Subscription{
		c:                c,
		viewID:           attach.ViewID,
		generation:       attach.Generation,
		logURL:           attach.LogURL,
		capability:       attach.Capability,
		capabilityHeader: attach.CapabilityHeader,
		region:           normalizeRegion(attach.Region),
		sessionToken:     c.SessionToken(),
		statement:        statement,
		checkpoint:       attach.Checkpoint,
		offset:           attachOffset(attach),
		events:           make(chan Update, 128),
		done:             make(chan struct{}),
		cancel:           cancel,
	}
	started := false
	defer func() {
		if !started {
			s.Close()
		}
	}()
	if sub.Transport == TransportWebSocket {
		parameterMap, err := canonicalSQLParameters(params)
		if err != nil {
			return nil, err
		}
		s.parameters, err = json.Marshal(parameterMap)
		if err != nil {
			return nil, fmt.Errorf("sdk: encode reactive parameters: %w", err)
		}
		conn, err := c.dialReactiveWebSocket(ctx, s)
		if err != nil {
			return nil, err
		}
		s.websocket = conn
		if err := writeReactiveSubscribe(ctx, conn, s); err != nil {
			_ = conn.Close(websocket.StatusProtocolError, "invalid subscribe")
			return nil, err
		}
	}
	for index, doc := range attach.Snapshot {
		cp := s.Checkpoint()
		if !s.deliver(Update{Kind: UpdateSnapshot, Checkpoint: cp, ID: doc.ID, Collection: doc.Collection, Body: cloneRaw(doc.Body), Position: uint32(index)}) {
			return nil, fmt.Errorf("sdk: initial snapshot exceeds subscription buffer")
		}
	}
	// Start polling only after the initial snapshot is queued, so a fast
	// origin cannot deliver a delta ahead of the snapshot. Cancellation and
	// terminal resets both release the subscription resources.
	s.wg.Add(1)
	if sub.Transport == TransportWebSocket {
		go s.websocketLoop(pollCtx)
	} else {
		go s.pollLoop(pollCtx)
	}
	go func() {
		select {
		case <-pollCtx.Done():
			s.Close()
		case <-s.done:
		}
	}()
	started = true
	return s, nil
}

func (c *Client) dialReactiveWebSocket(ctx context.Context, s *Subscription) (*websocket.Conn, error) {
	origin, err := reactiveOrigin(c)
	if err != nil {
		return nil, err
	}
	u, err := url.Parse(origin + canonicalReactivePath(s.viewID, s.generation, "websocket"))
	if err != nil {
		return nil, fmt.Errorf("sdk: invalid reactive WebSocket URL: %w", err)
	}
	if u.Scheme == "http" {
		u.Scheme = "ws"
	} else {
		u.Scheme = "wss"
	}
	if !validWebSocketToken(s.capability) {
		return nil, fmt.Errorf("sdk: attach response has invalid view capability")
	}
	header := make(http.Header)
	c.decorateScopeHeaders(header)
	if token := c.token(); token != "" {
		if strings.HasPrefix(token, "Bearer ") {
			header.Set("Authorization", token)
		} else {
			header.Set("Authorization", "Bearer "+token)
		}
	}
	if causal := strings.TrimSpace(s.sessionToken); causal != "" {
		header.Set("X-TerraScale-Session", causal)
	}
	conn, resp, err := websocket.Dial(ctx, u.String(), &websocket.DialOptions{
		HTTPClient: c.http,
		HTTPHeader: header,
		Subprotocols: []string{
			"terrascale.sync.v1",
			"terrascale.cap." + s.capability,
		},
	})
	if err != nil {
		return nil, fmt.Errorf("sdk: reactive WebSocket upgrade failed: %w", err)
	}
	if resp != nil {
		c.observeSession(resp.Header)
		s.sessionToken = c.SessionToken()
	}
	if conn.Subprotocol() != "terrascale.sync.v1" {
		_ = conn.Close(websocket.StatusProtocolError, "unsupported subprotocol")
		return nil, fmt.Errorf("sdk: reactive WebSocket negotiated unsupported subprotocol")
	}
	conn.SetReadLimit(maxReactiveBody)
	return conn, nil
}

func validWebSocketToken(value string) bool {
	if value == "" || len(value) > 4096 || strings.ContainsAny(value, "\r\n") {
		return false
	}
	for _, b := range []byte(value) {
		if b <= 0x20 || b >= 0x7f || strings.ContainsRune("()<>@,;:\\[\\]?={}\"/", rune(b)) {
			return false
		}
	}
	return true
}

func writeReactiveSubscribe(ctx context.Context, conn *websocket.Conn, s *Subscription) error {
	checkpoint := []byte(s.Checkpoint())
	body := mpArray(4)
	body = append(body, mpString(s.viewID)...)
	body = append(body, mpString(s.statement)...)
	body = append(body, mpBinaryValue(s.parameters)...)
	if len(checkpoint) == 0 {
		body = append(body, 0xc0)
	} else {
		body = append(body, mpBinaryValue(checkpoint)...)
	}
	frame := mpArray(7)
	for _, value := range []uint64{1, 0, 0, 1, 1, 0} {
		frame = append(frame, mpUintValue(value)...)
	}
	frame = append(frame, body...)
	if len(frame) > maxReactiveBody {
		return fmt.Errorf("sdk: reactive subscribe frame exceeds %d bytes", maxReactiveBody)
	}
	return conn.Write(ctx, websocket.MessageBinary, frame)
}

func (s *Subscription) websocketLoop(ctx context.Context) {
	defer s.wg.Done()
	defer close(s.events)
	defer s.cancel()
	for {
		typ, frame, err := s.websocket.Read(ctx)
		if err != nil {
			if ctx.Err() == nil {
				s.deliver(Update{Kind: UpdateReset, Checkpoint: s.Checkpoint(), Reset: &ResetError{Reason: ResetReconnect, Message: "reactive WebSocket connection closed", ViewID: s.viewID, Generation: s.generation}})
			}
			return
		}
		if typ != websocket.MessageBinary {
			_ = s.websocket.Close(websocket.StatusProtocolError, "binary MessagePack required")
			s.deliver(Update{Kind: UpdateReset, Checkpoint: s.Checkpoint(), Reset: &ResetError{Reason: ResetReconnect, Message: "reactive WebSocket sent a non-binary frame", ViewID: s.viewID, Generation: s.generation}})
			return
		}
		updates, cp, _, reset, err := decodeReactiveMessagePack(frame, s.viewID, s.generation)
		if err != nil {
			_ = s.websocket.Close(websocket.StatusProtocolError, "invalid reactive frame")
			s.deliver(Update{Kind: UpdateReset, Checkpoint: s.Checkpoint(), Reset: &ResetError{Reason: ResetReconnect, Message: "invalid reactive WebSocket frame", ViewID: s.viewID, Generation: s.generation}})
			return
		}
		if cp != "" {
			s.setProgress(cp, "", "")
		}
		if reset != nil {
			s.deliver(Update{Kind: UpdateReset, Checkpoint: s.Checkpoint(), Snapshot: cloneDocuments(reset.Snapshot), Reset: reset})
			return
		}
		for _, update := range updates {
			if update.Checkpoint == "" {
				update.Checkpoint = cp
			}
			if !s.deliver(update) {
				return
			}
		}
	}
}

func mpArray(n int) []byte { return []byte{0x90 | byte(n)} }

func mpUintValue(v uint64) []byte {
	if v <= 0x7f {
		return []byte{byte(v)}
	}
	if v <= 0xff {
		return []byte{0xcc, byte(v)}
	}
	if v <= 0xffff {
		return []byte{0xcd, byte(v >> 8), byte(v)}
	}
	if v <= 0xffffffff {
		return []byte{0xce, byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)}
	}
	return []byte{0xcf, byte(v >> 56), byte(v >> 48), byte(v >> 40), byte(v >> 32), byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)}
}

func mpString(v string) []byte {
	b := []byte(v)
	if len(b) < 32 {
		return append([]byte{0xa0 | byte(len(b))}, b...)
	}
	if len(b) <= 65535 {
		return append([]byte{0xda, byte(len(b) >> 8), byte(len(b))}, b...)
	}
	return append([]byte{0xdb, byte(len(b) >> 24), byte(len(b) >> 16), byte(len(b) >> 8), byte(len(b))}, b...)
}

func mpBinaryValue(v []byte) []byte {
	return append([]byte{0xc6, byte(len(v) >> 24), byte(len(v) >> 16), byte(len(v) >> 8), byte(len(v))}, v...)
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
	if sub.RegionHint != "" {
		region, err := ParseRegionCode(string(sub.RegionHint))
		if err != nil {
			return nil, err
		}
		opts = append(opts, WithRegionHint(region))
	}
	req := attachRequest{
		DatabaseID: databaseID,
		Statement:  statement,
	}
	parameterMap, err := canonicalSQLParameters(params)
	if err != nil {
		return nil, err
	}
	parameterBytes, err := json.Marshal(parameterMap)
	if err != nil {
		return nil, fmt.Errorf("sdk: encode reactive parameters: %w", err)
	}
	req.Parameters = base64.StdEncoding.EncodeToString(parameterBytes)
	if sub.ResumeFrom != "" {
		checkpoint := Checkpoint(base64.StdEncoding.EncodeToString([]byte(sub.ResumeFrom)))
		req.Checkpoint = &checkpoint
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

// decodeCheckpoint converts the producer's base64 checkpoint spelling to the
// SDK's opaque byte-string representation. Plain strings remain accepted for
// older local fixtures that predate the published attach contract.
func decodeCheckpoint(value Checkpoint) Checkpoint {
	if value == "" {
		return ""
	}
	decoded, err := base64.StdEncoding.DecodeString(string(value))
	if err != nil || len(decoded) == 0 {
		return value
	}
	return Checkpoint(string(decoded))
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
	// Checkpoints are opaque bytes and are never valid log offsets. A view
	// without an explicit revision starts at the canonical zero offset.
	return "0"
}

func normalizeCapabilityHeader(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return defaultCapabilityHeader, nil
	}
	if strings.EqualFold(value, defaultCapabilityHeader) {
		return defaultCapabilityHeader, nil
	}
	return "", fmt.Errorf("sdk: attach response must advertise %q, got %q", defaultCapabilityHeader, value)
}

func validateCapability(value string) error {
	if strings.TrimSpace(value) == "" || len(value) > 4096 || strings.ContainsAny(value, "\r\n") {
		return fmt.Errorf("sdk: attach response has invalid view capability")
	}
	return nil
}

func normalizeRegion(value string) string {
	code, err := ParseRegionCode(value)
	if err != nil {
		return ""
	}
	return string(code)
}

func reactiveOrigin(c *Client) (string, error) {
	origin := c.APIBaseURL()
	if origin == DefaultAPIBaseURL {
		origin = DefaultSyncBaseURL
	}
	u, err := url.Parse(origin)
	if err != nil || u.Scheme != "http" && u.Scheme != "https" || u.Host == "" {
		return "", fmt.Errorf("sdk: invalid reactive origin")
	}
	u.Path, u.RawPath, u.RawQuery, u.Fragment = "", "", "", ""
	return strings.TrimSuffix(u.String(), "/"), nil
}

func canonicalReactivePath(view string, generation uint64, leaf string) string {
	return "/api/v1/reactive/views/" + url.PathEscape(view) + "/" + strconv.FormatUint(generation, 10) + "/" + leaf
}

func canonicalLogURL(c *Client, advertised, view string, generation uint64) (string, error) {
	origin, err := reactiveOrigin(c)
	if err != nil {
		return "", err
	}
	u, err := url.Parse(strings.TrimSpace(advertised))
	if err != nil || u.IsAbs() && u.Scheme != "http" && u.Scheme != "https" {
		return "", fmt.Errorf("sdk: attach response has invalid log URL")
	}
	if u.IsAbs() {
		base, _ := url.Parse(origin)
		if !strings.EqualFold(u.Host, base.Host) || !strings.EqualFold(u.Scheme, base.Scheme) {
			return origin + canonicalReactivePath(view, generation, "log"), nil
		}
	}
	// The origin only advertises capability-bearing URLs; the client derives
	// the shared cache key and never trusts an origin-selected path/query.
	return origin + canonicalReactivePath(view, generation, "log"), nil
}

func canonicalSnapshotURL(c *Client, advertised, view string, generation uint64, index int) (string, error) {
	origin, err := reactiveOrigin(c)
	if err != nil {
		return "", err
	}
	u, err := url.Parse(strings.TrimSpace(advertised))
	if err != nil || u.IsAbs() && u.Scheme != "http" && u.Scheme != "https" {
		return "", fmt.Errorf("sdk: invalid snapshot URL")
	}
	if u.IsAbs() {
		base, _ := url.Parse(origin)
		if !strings.EqualFold(u.Host, base.Host) || !strings.EqualFold(u.Scheme, base.Scheme) {
			u = nil
		}
	}
	if u != nil && (u.Path == "" || strings.HasSuffix(u.Path, "/snapshot/"+strconv.Itoa(index))) {
		return origin + u.EscapedPath(), nil
	}
	return origin + canonicalReactivePath(view, generation, "snapshot/"+strconv.Itoa(index)), nil
}

func (c *Client) fetchSnapshotChunks(ctx context.Context, attach *attachResponse) ([]Document, Checkpoint, error) {
	if len(attach.SnapshotChunks) > maxSnapshotChunks {
		return nil, "", fmt.Errorf("sdk: reactive snapshot has too many chunks")
	}
	var docs []Document
	checkpoint := attach.Checkpoint
	totalBytes := 0
	for i, advertised := range attach.SnapshotChunks {
		chunkURL, err := canonicalSnapshotURL(c, advertised, attach.ViewID, attach.Generation, i)
		if err != nil {
			return nil, "", err
		}
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, chunkURL, nil)
		if err != nil {
			return nil, "", err
		}
		req.Header.Set("Accept", "application/msgpack")
		c.decorateScopeHeaders(req.Header)
		capHeader, _ := normalizeCapabilityHeader(attach.CapabilityHeader)
		req.Header.Set(capHeader, attach.Capability)
		if token := c.token(); token != "" {
			if strings.HasPrefix(token, "Bearer ") {
				req.Header.Set("Authorization", token)
			} else {
				req.Header.Set("Authorization", "Bearer "+token)
			}
		}
		if causal := strings.TrimSpace(c.SessionToken()); causal != "" {
			req.Header.Set("X-TerraScale-Session", causal)
		}
		resp, err := c.http.Do(req)
		if err != nil {
			return nil, "", err
		}
		c.observeSession(resp.Header)
		body, readErr := io.ReadAll(io.LimitReader(resp.Body, maxReactiveBody+1))
		resp.Body.Close()
		if readErr != nil {
			return nil, "", readErr
		}
		if len(body) > maxReactiveBody {
			return nil, "", fmt.Errorf("sdk: reactive snapshot chunk exceeds %d bytes", maxReactiveBody)
		}
		if resp.StatusCode >= 400 {
			return nil, "", resetForStatusError(resp.StatusCode, chunkURL, body)
		}
		chunkDocs, cp, _, decodeErr := decodeReactiveSnapshotMessagePack(body, attach.ViewID, attach.Generation)
		if decodeErr != nil {
			return nil, "", decodeErr
		}
		docs = append(docs, chunkDocs...)
		if len(docs) > maxSnapshotItems {
			return nil, "", fmt.Errorf("sdk: reactive snapshot exceeds %d documents", maxSnapshotItems)
		}
		for _, doc := range chunkDocs {
			totalBytes += len(doc.Body)
			if totalBytes > maxSnapshotBytes {
				return nil, "", fmt.Errorf("sdk: reactive snapshot exceeds %d bytes", maxSnapshotBytes)
			}
		}
		if cp != "" {
			checkpoint = cp
		}
	}
	return docs, checkpoint, nil
}

func (c *Client) advanceSessionToken(token string) {
	if strings.TrimSpace(token) == "" {
		return
	}
	_ = c.SetSessionToken(token)
}

func resetForStatusError(status int, path string, body []byte) error {
	if reset := resetForStatus(status); reset != nil {
		return reset
	}
	return &APIError{Method: http.MethodGet, Path: path, Status: status, Message: string(body)}
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
			if errors.Is(err, context.DeadlineExceeded) {
				continue
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
			if prog != "" {
				cp = prog
			}
			s.deliver(Update{Kind: UpdateReset, Checkpoint: cp, Snapshot: cloneDocuments(reset.Snapshot), Reset: reset})
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
	maxSnapshotChunks       = 10000
	maxSnapshotItems        = 10000
	maxSnapshotBytes        = 8 << 20
	defaultCapabilityHeader = "X-TerraScale-Capability"
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
	// The protocol's long-poll window is bounded at 25 seconds, regardless of
	// the caller's (possibly much longer) HTTP client timeout.
	pollCtx, cancel := context.WithTimeout(ctx, 25*time.Second)
	defer cancel()
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
	req, err := http.NewRequestWithContext(pollCtx, "GET", u.String(), nil)
	if err != nil {
		return nil, "", "", "", nil, err
	}
	req.Header.Set("Accept", "application/msgpack")
	s.c.decorateScopeHeaders(req.Header)
	if s.capability != "" {
		header := s.capabilityHeader
		if header == "" {
			header = defaultCapabilityHeader
		}
		req.Header.Set(header, s.capability)
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
	if causal := strings.TrimSpace(s.sessionToken); causal != "" {
		req.Header.Set("X-TerraScale-Session", causal)
	}
	resp, err := s.c.http.Do(req)
	if err != nil {
		return nil, "", "", "", nil, err
	}
	defer resp.Body.Close()
	s.c.observeSession(resp.Header)
	s.sessionToken = s.c.SessionToken()
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
	if resp.StatusCode == http.StatusNoContent || len(bytes.TrimSpace(body)) == 0 && resp.StatusCode < 400 {
		// A long-poll timeout is deliberately empty and never carries an ETag.
		return nil, "", "", "", nil, nil
	}
	contentType := strings.ToLower(strings.TrimSpace(strings.Split(resp.Header.Get("Content-Type"), ";")[0]))
	if contentType == "application/msgpack" {
		if resp.StatusCode >= 400 {
			if reset := resetForStatus(resp.StatusCode); reset != nil {
				reset.ViewID, reset.Generation = s.viewID, s.generation
				if resp.StatusCode == http.StatusMisdirectedRequest {
					s.region = normalizeRegion(resp.Header.Get("X-TerraScale-Placement"))
				}
				return nil, "", "", "", reset, nil
			}
			return nil, "", "", "", nil, errorFromResponse("reactive.log", http.MethodGet, u.Path, resp.StatusCode, resp.Header, body)
		}
		updates, cp, nextOffset, reset, decodeErr := decodeReactiveMessagePack(body, s.viewID, s.generation)
		if decodeErr != nil {
			return nil, "", "", "", nil, decodeErr
		}
		if reset != nil {
			if cp != "" {
				s.setProgress(cp, nextOffset, "")
			}
			return nil, cp, nextOffset, "", reset, nil
		}
		responseETag := resp.Header.Get("ETag")
		if cacheNoStore(resp.Header.Get("Cache-Control")) || cacheNoStore(resp.Header.Get("Cloudflare-CDN-Cache-Control")) {
			responseETag = ""
		}
		return updates, cp, nextOffset, responseETag, nil, nil
	}
	if resp.StatusCode >= 400 {
		if reset := resetForStatus(resp.StatusCode); reset != nil {
			reset.ViewID, reset.Generation = s.viewID, s.generation
			if resp.StatusCode == http.StatusMisdirectedRequest {
				if region := normalizeRegion(resp.Header.Get("X-TerraScale-Placement")); region != "" {
					s.region = region
					reset.Message = "reactive view moved to region " + region
				}
			}
			return nil, "", "", "", reset, nil
		}
		return nil, "", "", "", nil, errorFromResponse("reactive.log", http.MethodGet, u.Path, resp.StatusCode, resp.Header, body)
	}
	return nil, "", "", "", nil, fmt.Errorf("sdk: reactive log returned unsupported content type %q", contentType)
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
	case "retention_advanced", "retention-advanced":
		reset.Reason = ResetRetentionAdvanced
	case "checkpoint_not_current", "checkpoint-not-current":
		reset.Reason = ResetCheckpointCurrent
	case "checkpoint-expired":
		reset.Reason = ResetExpired
	case "generation-changed", "generation_changed":
		reset.Reason = ResetGeneration
	case "schema_changed", "schema-changed":
		reset.Reason = ResetSchemaChanged
	case "region_moved", "region-moved":
		reset.Reason = ResetRegionMoved
	case "auth_changed", "authentication_expired", "permission_denied":
		reset.Reason = ResetAuthChanged
	case "backpressure", "slow_consumer":
		reset.Reason = ResetBackpressure
	}
	return reset
}

func resetForStatus(status int) *ResetError {
	switch status {
	case http.StatusMisdirectedRequest:
		return &ResetError{Reason: ResetRegionMoved, Message: "reactive view moved to another region"}
	case http.StatusUnauthorized, http.StatusForbidden:
		return &ResetError{Reason: ResetAuthChanged, Message: "view capability is no longer authorized"}
	case http.StatusNotFound:
		return &ResetError{Reason: ResetGeneration, Message: "view generation is no longer available"}
	case http.StatusConflict:
		return &ResetError{Reason: ResetCheckpointCurrent, Message: "log offset is no longer current"}
	case http.StatusGone:
		return &ResetError{Reason: ResetHistoryExpired, Message: "log history is no longer retained"}
	case http.StatusTooManyRequests:
		return &ResetError{Reason: ResetBackpressure, Message: "reactive consumer is over the server backpressure limit"}
	default:
		return nil
	}
}

// The sync-v1 codec is deliberately local and dependency-free. MessagePack
// maps, extensions, floats, and non-indexed shapes are never accepted.
type reactiveMPReader struct {
	b []byte
	i int
}

func (r *reactiveMPReader) take(n int) ([]byte, error) {
	if n < 0 || r.i > len(r.b)-n {
		return nil, fmt.Errorf("sdk: reactive MessagePack is truncated")
	}
	v := r.b[r.i : r.i+n]
	r.i += n
	return v, nil
}
func (r *reactiveMPReader) tag() (byte, error) {
	v, err := r.take(1)
	if err != nil {
		return 0, err
	}
	return v[0], nil
}
func mpArrayCount(r *reactiveMPReader) (int, error) {
	t, err := r.tag()
	if err != nil {
		return 0, err
	}
	switch {
	case t >= 0x90 && t <= 0x9f:
		return int(t - 0x90), nil
	case t == 0xdc:
		v, e := r.take(2)
		if e != nil {
			return 0, e
		}
		return int(v[0])<<8 | int(v[1]), nil
	case t == 0xdd:
		v, e := r.take(4)
		if e != nil {
			return 0, e
		}
		n := uint64(v[0])<<24 | uint64(v[1])<<16 | uint64(v[2])<<8 | uint64(v[3])
		if n > uint64(^uint(0)>>1) {
			return 0, fmt.Errorf("sdk: reactive array length overflows")
		}
		return int(n), nil
	}
	return 0, fmt.Errorf("sdk: reactive value is not an indexed array")
}
func (r *reactiveMPReader) exactArray(n int) error {
	got, err := mpArrayCount(r)
	if err != nil {
		return err
	}
	if got != n {
		return fmt.Errorf("sdk: reactive indexed array has %d fields, want %d", got, n)
	}
	return nil
}
func mpUint(r *reactiveMPReader, bits int) (uint64, error) {
	t, err := r.tag()
	if err != nil {
		return 0, err
	}
	var v uint64
	switch {
	case t <= 0x7f:
		v = uint64(t)
	case t == 0xcc:
		x, e := r.take(1)
		if e != nil {
			return 0, e
		}
		v = uint64(x[0])
	case t == 0xcd:
		x, e := r.take(2)
		if e != nil {
			return 0, e
		}
		v = uint64(x[0])<<8 | uint64(x[1])
	case t == 0xce:
		x, e := r.take(4)
		if e != nil {
			return 0, e
		}
		v = uint64(x[0])<<24 | uint64(x[1])<<16 | uint64(x[2])<<8 | uint64(x[3])
	case t == 0xcf:
		x, e := r.take(8)
		if e != nil {
			return 0, e
		}
		for _, b := range x {
			v = v<<8 | uint64(b)
		}
	case t >= 0xe0 || (t >= 0xd0 && t <= 0xd3):
		return 0, fmt.Errorf("sdk: reactive integer is negative")
	default:
		return 0, fmt.Errorf("sdk: reactive value is not an unsigned integer")
	}
	if bits < 64 && v >= uint64(1)<<bits {
		return 0, fmt.Errorf("sdk: reactive integer exceeds u%d", bits)
	}
	return v, nil
}
func mpText(r *reactiveMPReader, min, max int) (string, error) {
	t, err := r.tag()
	if err != nil {
		return "", err
	}
	var n int
	switch {
	case t >= 0xa0 && t <= 0xbf:
		n = int(t - 0xa0)
	case t == 0xd9:
		x, e := r.take(1)
		if e != nil {
			return "", e
		}
		n = int(x[0])
	case t == 0xda:
		x, e := r.take(2)
		if e != nil {
			return "", e
		}
		n = int(x[0])<<8 | int(x[1])
	case t == 0xdb:
		x, e := r.take(4)
		if e != nil {
			return "", e
		}
		nn := uint64(x[0])<<24 | uint64(x[1])<<16 | uint64(x[2])<<8 | uint64(x[3])
		if nn > uint64(max) {
			return "", fmt.Errorf("sdk: reactive string exceeds bound")
		}
		n = int(nn)
	default:
		return "", fmt.Errorf("sdk: reactive value is not text")
	}
	if n < min || n > max {
		return "", fmt.Errorf("sdk: reactive string exceeds bound")
	}
	b, e := r.take(n)
	if e != nil {
		return "", e
	}
	if !utf8.Valid(b) {
		return "", fmt.Errorf("sdk: reactive text is not UTF-8")
	}
	return string(b), nil
}
func mpBinary(r *reactiveMPReader, min, max int) ([]byte, error) {
	t, e := r.tag()
	if e != nil {
		return nil, e
	}
	var n int
	switch {
	case t == 0xc4:
		x, e := r.take(1)
		if e != nil {
			return nil, e
		}
		n = int(x[0])
	case t == 0xc5:
		x, e := r.take(2)
		if e != nil {
			return nil, e
		}
		n = int(x[0])<<8 | int(x[1])
	case t == 0xc6:
		x, e := r.take(4)
		if e != nil {
			return nil, e
		}
		nn := uint64(x[0])<<24 | uint64(x[1])<<16 | uint64(x[2])<<8 | uint64(x[3])
		if nn > uint64(max) {
			return nil, fmt.Errorf("sdk: reactive binary exceeds bound")
		}
		n = int(nn)
	default:
		return nil, fmt.Errorf("sdk: reactive value is not binary")
	}
	if n < min || n > max {
		return nil, fmt.Errorf("sdk: reactive binary exceeds bound")
	}
	b, e := r.take(n)
	return append([]byte(nil), b...), e
}
func mpNil(r *reactiveMPReader) error {
	t, e := r.tag()
	if e != nil {
		return e
	}
	if t != 0xc0 {
		return fmt.Errorf("sdk: reactive optional value is not nil")
	}
	return nil
}

func scanReactiveMessagePack(b []byte) error {
	if len(b) == 0 || len(b) > maxReactiveBody {
		return fmt.Errorf("sdk: reactive MessagePack exceeds %d bytes", maxReactiveBody)
	}
	s := reactiveMPReader{b: b}
	if err := scanMPValue(&s, 0); err != nil {
		return err
	}
	if s.i != len(b) {
		return fmt.Errorf("sdk: reactive MessagePack has trailing bytes")
	}
	return validateReactiveProtocol(b)
}

func validateReactiveProtocol(b []byte) error {
	r := reactiveMPReader{b: b}
	count, err := mpArrayCount(&r)
	if err != nil {
		return err
	}
	if count == 7 && looksLikeProtocolMajor(b[r.i:]) {
		r = reactiveMPReader{b: b}
		_, err = decodeMPEnvelope(&r, "", 0)
		return err
	}
	if count > maxSnapshotItems {
		return fmt.Errorf("sdk: reactive frame list array exceeds bound")
	}
	for i := 0; i < count; i++ {
		if _, err = decodeMPEnvelope(&r, "", 0); err != nil {
			return err
		}
	}
	if r.i != len(b) {
		return fmt.Errorf("sdk: reactive MessagePack has trailing bytes")
	}
	return nil
}
func scanMPValue(r *reactiveMPReader, depth int) error {
	if depth > 64 {
		return fmt.Errorf("sdk: reactive MessagePack nesting exceeds 64")
	}
	t, e := r.tag()
	if e != nil {
		return e
	}
	switch {
	case t <= 0x7f || t >= 0xe0 || t == 0xc0 || t == 0xc2 || t == 0xc3:
		return nil
	case t >= 0x90 && t <= 0x9f:
		n := int(t - 0x90)
		if n > maxSnapshotItems {
			return fmt.Errorf("sdk: reactive array exceeds bound")
		}
		for i := 0; i < n; i++ {
			if e := scanMPValue(r, depth+1); e != nil {
				return e
			}
		}
		return nil
	case t >= 0xa0 && t <= 0xbf:
		return scanTakeBounded(r, int(t-0xa0), 262144)
	case t >= 0x80 && t <= 0x8f || t == 0xde || t == 0xdf:
		return fmt.Errorf("sdk: reactive MessagePack maps are forbidden")
	case t >= 0xc4 && t <= 0xc6:
		n, e := scanLength(r, []byte{1, 2, 4}[t-0xc4])
		if e != nil {
			return e
		}
		if n > 1<<20 {
			return fmt.Errorf("sdk: reactive binary exceeds 1 MiB")
		}
		return scanTakeBounded(r, n, 1<<20)
	case t >= 0xc7 && t <= 0xc9 || t >= 0xd4 && t <= 0xd8:
		return fmt.Errorf("sdk: reactive MessagePack extensions are forbidden")
	case t == 0xca || t == 0xcb:
		return fmt.Errorf("sdk: reactive MessagePack floats are forbidden")
	case t >= 0xcc && t <= 0xcf:
		return scanTakeBounded(r, []int{1, 2, 4, 8}[t-0xcc], 8)
	case t >= 0xd0 && t <= 0xd3:
		return scanTakeBounded(r, []int{1, 2, 4, 8}[t-0xd0], 8)
	case t >= 0xd9 && t <= 0xdb:
		n, e := scanLength(r, []byte{1, 2, 4}[t-0xd9])
		if e != nil {
			return e
		}
		return scanTakeBounded(r, n, 262144)
	case t == 0xdc || t == 0xdd:
		n, e := scanLength(r, t-0xdc+2)
		if e != nil {
			return e
		}
		if n > maxSnapshotItems {
			return fmt.Errorf("sdk: reactive array exceeds bound")
		}
		for i := 0; i < n; i++ {
			if e := scanMPValue(r, depth+1); e != nil {
				return e
			}
		}
		return nil
	default:
		return fmt.Errorf("sdk: reactive MessagePack tag 0x%x is invalid", t)
	}
}
func scanLength(r *reactiveMPReader, width byte) (int, error) {
	b, e := r.take(int(width))
	if e != nil {
		return 0, e
	}
	var n uint64
	for _, v := range b {
		n = n<<8 | uint64(v)
	}
	if n > uint64(^uint(0)>>1) {
		return 0, fmt.Errorf("sdk: reactive length overflows")
	}
	return int(n), nil
}
func scanTakeBounded(r *reactiveMPReader, n, max int) error {
	if n < 0 || n > max {
		return fmt.Errorf("sdk: reactive value exceeds bound")
	}
	_, e := r.take(n)
	return e
}

type reactiveMPMessage struct {
	updates                 []Update
	checkpoint              Checkpoint
	checkpointBytes         []byte
	offset                  string
	reset                   *ResetError
	snapshot                []Document
	snapshotFields          []map[string]json.RawMessage
	viewGeneration          uint64
	authorizationGeneration uint64
	revision                uint64
	fromRevision            uint64
	toRevision              uint64
	digest                  string
}

func decodeReactiveMessagePack(body []byte, view string, generation uint64) ([]Update, Checkpoint, string, *ResetError, error) {
	if err := scanReactiveMessagePack(body); err != nil {
		return nil, "", "", nil, err
	}
	r := reactiveMPReader{b: body}
	count, err := mpArrayCount(&r)
	if err != nil {
		return nil, "", "", nil, err
	}
	if count != 7 || !looksLikeProtocolMajor(body[r.i:]) {
		return nil, "", "", nil, fmt.Errorf("sdk: reactive response must contain one indexed envelope")
	}
	r = reactiveMPReader{b: body}
	m, err := decodeMPEnvelope(&r, view, generation)
	if err != nil {
		return nil, "", "", nil, err
	}
	messages := []reactiveMPMessage{m}
	var updates []Update
	var cp Checkpoint
	var offset string
	var reset *ResetError
	for _, m := range messages {
		updates = append(updates, m.updates...)
		if m.checkpoint != "" {
			cp = m.checkpoint
		}
		if m.offset != "" {
			offset = m.offset
		}
		if m.reset != nil {
			reset = m.reset
		}
	}
	return updates, cp, offset, reset, nil
}

func looksLikeProtocolMajor(b []byte) bool {
	if len(b) == 0 {
		return false
	}
	if b[0] == 1 {
		return true
	}
	return len(b) >= 2 && b[0] == 0xcc && b[1] == 1
}
func decodeReactiveSnapshotMessagePack(body []byte, view string, generation uint64) ([]Document, Checkpoint, string, error) {
	if err := scanReactiveMessagePack(body); err != nil {
		return nil, "", "", err
	}
	r := reactiveMPReader{b: body}
	count, err := mpArrayCount(&r)
	if err != nil {
		return nil, "", "", err
	}
	if count != 7 || !looksLikeProtocolMajor(body[r.i:]) {
		return nil, "", "", fmt.Errorf("sdk: snapshot response must contain one indexed envelope")
	}
	r = reactiveMPReader{b: body}
	m, err := decodeMPEnvelope(&r, view, generation)
	if err != nil {
		return nil, "", "", err
	}
	messages := []reactiveMPMessage{m}
	var docs []Document
	var cp Checkpoint
	var off string
	seenSnapshot := false
	for _, m := range messages {
		if m.snapshot != nil {
			seenSnapshot = true
		}
		docs = append(docs, m.snapshot...)
		if m.reset != nil {
			seenSnapshot = true
			docs = append(docs, m.reset.Snapshot...)
		}
		if m.checkpoint != "" {
			cp = m.checkpoint
		}
		if m.offset != "" {
			off = m.offset
		}
	}
	if !seenSnapshot {
		return nil, cp, off, fmt.Errorf("sdk: snapshot chunk has no snapshot frame")
	}
	return docs, cp, off, nil
}
func decodeMPEnvelope(r *reactiveMPReader, view string, generation uint64) (reactiveMPMessage, error) {
	if err := r.exactArray(7); err != nil {
		return reactiveMPMessage{}, err
	}
	major, e := mpUint(r, 32)
	if e != nil {
		return reactiveMPMessage{}, e
	}
	minor, e := mpUint(r, 32)
	if e != nil {
		return reactiveMPMessage{}, e
	}
	features, e := mpUint(r, 32)
	if e != nil {
		return reactiveMPMessage{}, e
	}
	tag, e := mpUint(r, 32)
	if e != nil {
		return reactiveMPMessage{}, e
	}
	if major != 1 || minor != 0 || features != 0 {
		return reactiveMPMessage{}, fmt.Errorf("sdk: unsupported reactive MessagePack envelope")
	}
	if _, e = mpUint(r, 64); e != nil {
		return reactiveMPMessage{}, e
	}
	if _, e = mpUint(r, 64); e != nil {
		return reactiveMPMessage{}, e
	}
	return decodeMPBody(r, int(tag), view, generation)
}

type reactiveMPSnapshotMetadata struct {
	viewGeneration          uint64
	authorizationGeneration uint64
	revision                uint64
	digest                  string
}

func decodeMPSnapshot(r *reactiveMPReader, view string, generation uint64, metadata *reactiveMPSnapshotMetadata) ([]Document, []map[string]json.RawMessage, Checkpoint, error) {
	if err := r.exactArray(6); err != nil {
		return nil, nil, "", err
	}
	gotGeneration, e := mpUint(r, 64)
	if e != nil {
		return nil, nil, "", e
	}
	if generation != 0 && gotGeneration != generation {
		return nil, nil, "", fmt.Errorf("sdk: reactive snapshot generation mismatch")
	}
	authorizationGeneration, e := mpUint(r, 64)
	if e != nil {
		return nil, nil, "", e
	}
	revision, e := mpUint(r, 64)
	if e != nil {
		return nil, nil, "", e
	}
	n, e := mpArrayCount(r)
	if e != nil {
		return nil, nil, "", e
	}
	if n > maxSnapshotItems {
		return nil, nil, "", fmt.Errorf("sdk: reactive snapshot exceeds %d documents", maxSnapshotItems)
	}
	docs := make([]Document, 0, n)
	allFields := make([]map[string]json.RawMessage, 0, n)
	total := 0
	for i := 0; i < n; i++ {
		if err := r.exactArray(2); err != nil {
			return nil, nil, "", err
		}
		id, e := mpText(r, 0, 256)
		if e != nil {
			return nil, nil, "", e
		}
		fields, e := mpArrayCount(r)
		if e != nil {
			return nil, nil, "", e
		}
		if fields > 1024 {
			return nil, nil, "", fmt.Errorf("sdk: reactive projected fields exceed bound")
		}
		var body json.RawMessage
		fieldValues := make(map[string]json.RawMessage, fields)
		fieldBytes := 0
		for j := 0; j < fields; j++ {
			if err := r.exactArray(2); err != nil {
				return nil, nil, "", err
			}
			path, e := mpText(r, 1, 256)
			if e != nil {
				return nil, nil, "", e
			}
			value, e := mpBinary(r, 0, 1<<20)
			if e != nil {
				return nil, nil, "", e
			}
			fieldBytes += len(value)
			if fieldBytes > 1<<20 {
				return nil, nil, "", fmt.Errorf("sdk: reactive document exceeds 1 MiB")
			}
			if _, exists := fieldValues[path]; exists {
				return nil, nil, "", fmt.Errorf("sdk: reactive snapshot contains duplicate projected field %q", path)
			}
			fieldValues[path] = cloneRaw(value)
			if path == "body" {
				body = cloneRaw(value)
			}
		}
		total += len(body)
		if total > maxSnapshotBytes {
			return nil, nil, "", fmt.Errorf("sdk: reactive snapshot exceeds %d bytes", maxSnapshotBytes)
		}
		docs = append(docs, Document{ID: id, Body: body})
		allFields = append(allFields, fieldValues)
	}
	digest, e := mpText(r, 0, 64)
	if e != nil {
		return nil, nil, "", e
	}
	cp, e := mpBinary(r, 1, 4096)
	if e != nil {
		return nil, nil, "", e
	}
	if metadata != nil {
		metadata.viewGeneration = gotGeneration
		metadata.authorizationGeneration = authorizationGeneration
		metadata.revision = revision
		metadata.digest = digest
	}
	return docs, allFields, Checkpoint(string(cp)), nil
}

func decodeMPOperation(r *reactiveMPReader) (Update, error) {
	n, e := mpArrayCount(r)
	if e != nil {
		return Update{}, e
	}
	if n < 1 || n > 4 {
		return Update{}, fmt.Errorf("sdk: reactive operation has invalid shape")
	}
	kind, e := mpUint(r, 32)
	if e != nil {
		return Update{}, e
	}
	switch kind {
	case 1:
		if n != 4 {
			return Update{}, fmt.Errorf("sdk: invalid insert operation")
		}
		id, e := mpText(r, 0, 256)
		if e != nil {
			return Update{}, e
		}
		position, e := mpUint(r, 32)
		if e != nil {
			return Update{}, e
		}
		body, fields, e := decodeMPFields(r)
		if e != nil {
			return Update{}, e
		}
		return Update{Kind: UpdateInsert, ID: id, Position: uint32(position), Body: body, Fields: fields}, nil
	case 2:
		if n != 3 {
			return Update{}, fmt.Errorf("sdk: invalid update operation")
		}
		id, e := mpText(r, 0, 256)
		if e != nil {
			return Update{}, e
		}
		body, fields, e := decodeMPFields(r)
		if e != nil {
			return Update{}, e
		}
		return Update{Kind: UpdateUpdate, ID: id, Body: body, Fields: fields}, nil
	case 3:
		if n != 3 {
			return Update{}, fmt.Errorf("sdk: invalid patch operation")
		}
		id, e := mpText(r, 0, 256)
		if e != nil {
			return Update{}, e
		}
		changes, e := mpArrayCount(r)
		if e != nil {
			return Update{}, e
		}
		if changes > 1024 {
			return Update{}, fmt.Errorf("sdk: reactive patch exceeds field bound")
		}
		var body json.RawMessage
		fieldsMap := make(map[string]json.RawMessage, changes)
		total := 0
		for i := 0; i < changes; i++ {
			if err := r.exactArray(2); err != nil {
				return Update{}, err
			}
			path, e := mpText(r, 1, 256)
			if e != nil {
				return Update{}, e
			}
			if _, exists := fieldsMap[path]; exists {
				return Update{}, fmt.Errorf("sdk: reactive patch contains duplicate field %q", path)
			}
			t, e := r.tag()
			if e != nil {
				return Update{}, e
			}
			if t == 0xc0 {
				fieldsMap[path] = nil
				continue
			}
			r.i--
			value, e := mpBinary(r, 0, 1<<20)
			if e != nil {
				return Update{}, e
			}
			total += len(value)
			if total > 1<<20 {
				return Update{}, fmt.Errorf("sdk: reactive patch exceeds 1 MiB")
			}
			if path == "body" {
				body = cloneRaw(value)
			}
			fieldsMap[path] = cloneRaw(value)
		}
		return Update{Kind: UpdatePatch, ID: id, Body: body, Fields: fieldsMap}, nil
	case 4:
		if n != 2 {
			return Update{}, fmt.Errorf("sdk: invalid remove operation")
		}
		id, e := mpText(r, 0, 256)
		if e != nil {
			return Update{}, e
		}
		return Update{Kind: UpdateRemove, ID: id}, nil
	case 5:
		if n != 3 {
			return Update{}, fmt.Errorf("sdk: invalid move operation")
		}
		id, e := mpText(r, 0, 256)
		if e != nil {
			return Update{}, e
		}
		position, e := mpUint(r, 32)
		if e != nil {
			return Update{}, e
		}
		return Update{Kind: UpdateMove, ID: id, Position: uint32(position)}, nil
	}
	return Update{}, fmt.Errorf("sdk: unknown reactive operation %d", kind)
}
func decodeMPFields(r *reactiveMPReader) (json.RawMessage, map[string]json.RawMessage, error) {
	n, e := mpArrayCount(r)
	if e != nil {
		return nil, nil, e
	}
	if n > 1024 {
		return nil, nil, fmt.Errorf("sdk: reactive projected fields exceed bound")
	}
	var body json.RawMessage
	fields := make(map[string]json.RawMessage, n)
	total := 0
	for i := 0; i < n; i++ {
		if err := r.exactArray(2); err != nil {
			return nil, nil, err
		}
		path, e := mpText(r, 1, 256)
		if e != nil {
			return nil, nil, e
		}
		if _, exists := fields[path]; exists {
			return nil, nil, fmt.Errorf("sdk: reactive operation contains duplicate projected field %q", path)
		}
		value, e := mpBinary(r, 0, 1<<20)
		if e != nil {
			return nil, nil, e
		}
		total += len(value)
		if total > 1<<20 {
			return nil, nil, fmt.Errorf("sdk: reactive operation exceeds 1 MiB")
		}
		fields[path] = cloneRaw(value)
		if path == "body" {
			body = cloneRaw(value)
		}
	}
	return body, fields, nil
}

func cloneProjectedFields(in map[string]json.RawMessage) map[string]json.RawMessage {
	if in == nil {
		return nil
	}
	out := make(map[string]json.RawMessage, len(in))
	for key, value := range in {
		out[key] = cloneRaw(value)
	}
	return out
}

func decodeMPBody(r *reactiveMPReader, tag int, view string, generation uint64) (reactiveMPMessage, error) {
	switch tag {
	case 1, 2, 7:
		return reactiveMPMessage{}, fmt.Errorf("sdk: reactive message tag %d is client-to-server", tag)
	case 3: // snapshot
		if err := r.exactArray(2); err != nil {
			return reactiveMPMessage{}, err
		}
		subscription, e := mpText(r, 0, 128)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		var metadata reactiveMPSnapshotMetadata
		docs, snapshotFields, cp, e := decodeMPSnapshot(r, view, generation, &metadata)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		u := make([]Update, 0, len(docs))
		for index, d := range docs {
			fields := map[string]json.RawMessage(nil)
			if len(u) < len(snapshotFields) {
				fields = cloneProjectedFields(snapshotFields[len(u)])
			}
			u = append(u, Update{Kind: UpdateSnapshot, Checkpoint: cp, CheckpointBytes: []byte(cp), ID: d.ID, Collection: d.Collection, Body: cloneRaw(d.Body), Fields: fields, Position: uint32(index), ViewGeneration: metadata.viewGeneration, AuthorizationGeneration: metadata.authorizationGeneration, Revision: metadata.revision, Digest: metadata.digest})
		}
		_ = subscription
		return reactiveMPMessage{updates: u, checkpoint: cp, checkpointBytes: []byte(cp), snapshot: docs, snapshotFields: snapshotFields, viewGeneration: metadata.viewGeneration, authorizationGeneration: metadata.authorizationGeneration, revision: metadata.revision, digest: metadata.digest}, nil
	case 4: // delta
		if err := r.exactArray(7); err != nil {
			return reactiveMPMessage{}, err
		}
		subscription, e := mpText(r, 0, 128)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		viewGeneration, e := mpUint(r, 64)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		from, e := mpUint(r, 64)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		to, e := mpUint(r, 64)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		if to <= from {
			return reactiveMPMessage{}, fmt.Errorf("sdk: reactive delta revision must advance")
		}
		n, e := mpArrayCount(r)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		if n > 1024 {
			return reactiveMPMessage{}, fmt.Errorf("sdk: reactive delta exceeds operation bound")
		}
		updates := make([]Update, 0, n)
		for i := 0; i < n; i++ {
			u, e := decodeMPOperation(r)
			if e != nil {
				return reactiveMPMessage{}, e
			}
			u.Checkpoint = ""
			updates = append(updates, u)
		}
		digest, e := mpText(r, 0, 64)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		cpBytes, e := mpBinary(r, 1, 4096)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		cp := Checkpoint(string(cpBytes))
		for i := range updates {
			updates[i].Checkpoint, updates[i].CheckpointBytes = cp, []byte(cp)
			updates[i].ViewGeneration = viewGeneration
			updates[i].FromRevision = from
			updates[i].ToRevision = to
			updates[i].Digest = digest
		}
		if generation != 0 && viewGeneration != generation {
			return reactiveMPMessage{}, fmt.Errorf("sdk: reactive delta generation mismatch")
		}
		_ = subscription
		return reactiveMPMessage{updates: updates, checkpoint: cp, checkpointBytes: []byte(cp), offset: strconv.FormatUint(to, 10), viewGeneration: viewGeneration, fromRevision: from, toRevision: to, digest: digest}, nil
	case 5: // reset with replacement snapshot
		if err := r.exactArray(3); err != nil {
			return reactiveMPMessage{}, err
		}
		subscription, e := mpText(r, 0, 128)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		reason, e := decodeMPResetReason(r)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		var metadata reactiveMPSnapshotMetadata
		docs, _, cp, e := decodeMPSnapshot(r, view, 0, &metadata)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		reset := &ResetError{Reason: reason, ViewID: view, Generation: generation, ViewGeneration: metadata.viewGeneration, AuthorizationGeneration: metadata.authorizationGeneration, Revision: metadata.revision, Digest: metadata.digest, Message: "reactive view requires a replacement snapshot"}
		reset.Snapshot = cloneDocuments(docs)
		reset.CheckpointBytes = []byte(cp)
		u := Update{Kind: UpdateReset, Checkpoint: cp, CheckpointBytes: []byte(cp), Snapshot: cloneDocuments(docs), Reset: reset, ViewGeneration: metadata.viewGeneration, AuthorizationGeneration: metadata.authorizationGeneration, Revision: metadata.revision, Digest: metadata.digest}
		_ = subscription
		return reactiveMPMessage{updates: []Update{u}, checkpoint: cp, checkpointBytes: []byte(cp), reset: reset, viewGeneration: metadata.viewGeneration, authorizationGeneration: metadata.authorizationGeneration, revision: metadata.revision, digest: metadata.digest}, nil
	case 6:
		if err := r.exactArray(0); err != nil {
			return reactiveMPMessage{}, err
		}
		return reactiveMPMessage{}, nil
	case 8: // authorization changed includes replacement snapshot
		if err := r.exactArray(4); err != nil {
			return reactiveMPMessage{}, err
		}
		subscription, e := mpText(r, 0, 128)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		authorizationGeneration, e := mpUint(r, 64)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		n, e := mpArrayCount(r)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		if n > maxSnapshotItems {
			return reactiveMPMessage{}, fmt.Errorf("sdk: reactive removed list exceeds bound")
		}
		removed := make([]string, 0, n)
		for i := 0; i < n; i++ {
			identity, e := mpText(r, 0, 256)
			if e != nil {
				return reactiveMPMessage{}, e
			}
			removed = append(removed, identity)
		}
		var metadata reactiveMPSnapshotMetadata
		docs, _, cp, e := decodeMPSnapshot(r, view, 0, &metadata)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		if metadata.authorizationGeneration < authorizationGeneration {
			metadata.authorizationGeneration = authorizationGeneration
		}
		reset := &ResetError{Reason: ResetAuthChanged, ViewID: view, Generation: generation, ViewGeneration: metadata.viewGeneration, AuthorizationGeneration: metadata.authorizationGeneration, Revision: metadata.revision, Digest: metadata.digest, Removed: removed, Message: "authorization changed; replacement snapshot supplied"}
		reset.Snapshot = cloneDocuments(docs)
		reset.CheckpointBytes = []byte(cp)
		_ = subscription
		u := Update{Kind: UpdateReset, Checkpoint: cp, CheckpointBytes: []byte(cp), Snapshot: cloneDocuments(docs), Reset: reset, ViewGeneration: metadata.viewGeneration, AuthorizationGeneration: metadata.authorizationGeneration, Revision: metadata.revision, Digest: metadata.digest}
		return reactiveMPMessage{updates: []Update{u}, checkpoint: cp, checkpointBytes: []byte(cp), reset: reset, viewGeneration: metadata.viewGeneration, authorizationGeneration: metadata.authorizationGeneration, revision: metadata.revision, digest: metadata.digest}, nil
	case 9:
		if err := r.exactArray(1); err != nil {
			return reactiveMPMessage{}, err
		}
		region, e := mpText(r, 1, 64)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		reset := &ResetError{Reason: ResetRegionMoved, ViewID: view, Generation: generation, Message: "reactive view moved to region " + normalizeRegion(region)}
		return reactiveMPMessage{reset: reset}, nil
	case 10:
		if err := r.exactArray(2); err != nil {
			return reactiveMPMessage{}, err
		}
		code, e := mpUint(r, 32)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		if code < 1 || code > 8 {
			return reactiveMPMessage{}, fmt.Errorf("sdk: unknown reactive error enum %d", code)
		}
		var detail string
		if r.i < len(r.b) && r.b[r.i] == 0xc0 {
			_ = mpNil(r)
		} else {
			detail, e = mpText(r, 0, 256)
			if e != nil {
				return reactiveMPMessage{}, e
			}
		}
		// Error enum values are distinct from reset-reason values. Errors
		// that do not identify recoverable view state require reconnecting;
		// never misclassify a generation error as an auth change.
		reason := ResetReconnect
		switch code {
		case 3, 4: // authentication_expired, permission_denied
			reason = ResetAuthChanged
		case 5: // checkpoint_refused
			reason = ResetCheckpointCurrent
		case 6: // slow_consumer
			reason = ResetBackpressure
		}
		reset := &ResetError{Reason: reason, ViewID: view, Generation: generation, Message: detail}
		return reactiveMPMessage{reset: reset}, nil
	case 11:
		if err := r.exactArray(1); err != nil {
			return reactiveMPMessage{}, err
		}
		mode, e := mpUint(r, 32)
		if e != nil {
			return reactiveMPMessage{}, e
		}
		if mode == 1 {
			return reactiveMPMessage{reset: &ResetError{Reason: ResetBackpressure, ViewID: view, Generation: generation, Message: "reactive server requested backpressure pause"}}, nil
		}
		if mode != 2 {
			return reactiveMPMessage{}, fmt.Errorf("sdk: unknown reactive backpressure mode")
		}
		return reactiveMPMessage{}, nil
	default:
		return reactiveMPMessage{}, fmt.Errorf("sdk: unknown reactive MessagePack message tag %d", tag)
	}
}
func decodeMPResetReason(r *reactiveMPReader) (ResetReason, error) {
	v, e := mpUint(r, 32)
	if e != nil {
		return "", e
	}
	switch v {
	case 1:
		return ResetExpired, nil
	case 2:
		return ResetRetentionAdvanced, nil
	case 3:
		return ResetGeneration, nil
	case 4:
		return ResetCheckpointCurrent, nil
	case 5:
		return ResetViewDiverged, nil
	case 6:
		return ResetOversized, nil
	case 7:
		return ResetInvalidOperations, nil
	case 8:
		return ResetInvalidCheckpoint, nil
	case 9:
		return ResetBackpressure, nil
	}
	return "", fmt.Errorf("sdk: unknown reactive reset reason %d", v)
}
