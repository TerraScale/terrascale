package sdk

import (
	"context"
	"fmt"
	"net/http"
	"slices"
	"sync"
)

type SubscriptionSnapshotRequest struct {
	Version        uint8          `json:"version"`
	Phase          string         `json:"phase"`
	Predicate      QueryPredicate `json:"predicate"`
	Limit          uint16         `json:"limit"`
	SnapshotCursor string         `json:"snapshot_cursor,omitempty"`
}
type SubscriptionPollRequest struct {
	Version    uint8          `json:"version"`
	Phase      string         `json:"phase"`
	Predicate  QueryPredicate `json:"predicate"`
	PollCursor string         `json:"poll_cursor"`
}
type SubscriptionSnapshot struct {
	Version            uint8         `json:"version"`
	Phase              string        `json:"phase"`
	Rows               []DocumentRow `json:"rows"`
	NextSnapshotCursor string        `json:"next_snapshot_cursor,omitempty"`
	PollCursor         string        `json:"poll_cursor,omitempty"`
}

func (s *SubscriptionSnapshot) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"version", "phase", "rows"}, []string{"next_snapshot_cursor", "poll_cursor"})
	if err != nil {
		return err
	}
	_, next := object["next_snapshot_cursor"]
	_, poll := object["poll_cursor"]
	if next == poll {
		return fmt.Errorf("sdk: snapshot requires exactly one continuation or poll cursor")
	}
	name := "poll_cursor"
	if next {
		name = "next_snapshot_cursor"
	}
	cursor, err := nativeString(object[name])
	if err != nil || !validCursor(cursor) {
		return fmt.Errorf("sdk: invalid subscription snapshot cursor")
	}
	type wire SubscriptionSnapshot
	var value wire
	if err := decodeNativeJSON(data, &value); err != nil {
		return err
	}
	*s = SubscriptionSnapshot(value)
	return nil
}

type SubscriptionEffect struct {
	Document UUID `json:"document"`
	// A null point means the document left this predicate's result set. It
	// does not assert deletion of the underlying document.
	Point *DocumentPoint `json:"point"`
}
type SubscriptionGroup struct {
	Sequence DecimalU64           `json:"sequence"`
	Effects  []SubscriptionEffect `json:"effects"`
}
type SubscriptionPoll struct {
	Version    uint8               `json:"version"`
	Phase      string              `json:"phase"`
	Groups     []SubscriptionGroup `json:"groups"`
	PollCursor string              `json:"poll_cursor"`
}

func (d *DocumentsClient) SubscriptionSnapshot(ctx context.Context, scope DocumentScope, collection UUID, request SubscriptionSnapshotRequest) (*SubscriptionSnapshot, error) {
	path, err := nativeCollectionPath(scope, collection)
	if err != nil {
		return nil, err
	}
	if request.Phase != "snapshot" {
		return nil, fmt.Errorf("sdk: subscription snapshot phase required")
	}
	if err := (QueryRequest{Version: request.Version, Predicate: request.Predicate, Limit: request.Limit, Cursor: request.SnapshotCursor}).validate(); err != nil {
		return nil, err
	}
	request, err = freezeNative(request)
	if err != nil {
		return nil, err
	}
	var result SubscriptionSnapshot
	if err := d.c.doNative(ctx, scope, nativeRead|nativeSubscriptions, http.MethodPost, path+"/subscriptions", request, &result, false); err != nil {
		return nil, err
	}
	if result.Version != 1 || result.Phase != "snapshot" || len(result.Rows) > int(request.Limit) {
		return nil, fmt.Errorf("sdk: invalid subscription snapshot version, phase or size")
	}
	if err := validateRows(scope, result.Rows); err != nil {
		return nil, err
	}
	if result.NextSnapshotCursor != "" && (result.NextSnapshotCursor == request.SnapshotCursor || len(result.Rows) == 0) {
		return nil, fmt.Errorf("sdk: snapshot cursor made no progress")
	}
	return &result, nil
}
func (d *DocumentsClient) SubscriptionPoll(ctx context.Context, scope DocumentScope, collection UUID, request SubscriptionPollRequest) (*SubscriptionPoll, error) {
	path, err := nativeCollectionPath(scope, collection)
	if err != nil {
		return nil, err
	}
	if request.Version != 1 || request.Phase != "poll" || !validCursor(request.PollCursor) {
		return nil, fmt.Errorf("sdk: native poll version, phase or cursor")
	}
	if err := request.Predicate.validate(); err != nil {
		return nil, err
	}
	request, err = freezeNative(request)
	if err != nil {
		return nil, err
	}
	var result SubscriptionPoll
	if err := d.c.doNative(ctx, scope, nativeRead|nativeSubscriptions, http.MethodPost, path+"/subscriptions", request, &result, false); err != nil {
		return nil, err
	}
	if result.Version != 1 || result.Phase != "poll" || !validCursor(result.PollCursor) || len(result.Groups) > 8 {
		return nil, fmt.Errorf("sdk: invalid native poll response")
	}
	var previous DecimalU64
	for _, group := range result.Groups {
		if group.Sequence == 0 || group.Sequence <= previous || len(group.Effects) > 128 {
			return nil, fmt.Errorf("sdk: invalid atomic subscription group")
		}
		previous = group.Sequence
		var previousDocument UUID
		for _, effect := range group.Effects {
			if _, err := ParseUUID(string(effect.Document)); err != nil {
				return nil, err
			}
			if effect.Document <= previousDocument {
				return nil, fmt.Errorf("sdk: subscription effects must increase")
			}
			previousDocument = effect.Document
			if effect.Point != nil {
				if effect.Point.State != PointValue && effect.Point.State != PointConflict {
					return nil, fmt.Errorf("sdk: subscription entry must be live, conflict or null")
				}
				if err := effect.Point.validate(scope); err != nil {
					return nil, err
				}
			}
		}
	}
	if len(result.Groups) > 0 && result.PollCursor == request.PollCursor {
		return nil, fmt.Errorf("sdk: poll cursor made no progress")
	}
	return &result, nil
}

// Subscription binds all continuations to one exact scope, collection and
// predicate. Next delivers complete snapshot pages or atomic transaction groups
// synchronously; its context controls cancellation and no worker is spawned.
type Subscription struct {
	mu                  sync.Mutex
	documents           *DocumentsClient
	scope               DocumentScope
	collection          UUID
	predicate           QueryPredicate
	limit               uint16
	snapshotCursor      string
	pollCursor          string
	lastSequence        DecimalU64
	lastDocument        UUID
	seenSnapshotCursors []string
}
type SubscriptionBatch struct {
	Snapshot *SubscriptionSnapshot
	Poll     *SubscriptionPoll
}

func (d *DocumentsClient) Subscribe(scope DocumentScope, collection UUID, predicate QueryPredicate, limit uint16) (*Subscription, error) {
	if _, err := nativeCollectionPath(scope, collection); err != nil {
		return nil, err
	}
	if err := (QueryRequest{Version: 1, Predicate: predicate, Limit: limit}).validate(); err != nil {
		return nil, err
	}
	frozen, err := freezeNative(predicate)
	if err != nil {
		return nil, err
	}
	return &Subscription{documents: d, scope: scope, collection: collection, predicate: frozen, limit: limit}, nil
}

// ResumeSubscription retains an opaque poll cursor and binds subsequent calls
// to the caller's exact tuple and predicate. The producer verifies its scoped
// signature and retention lease; the SDK never translates cursor contents.
func (d *DocumentsClient) ResumeSubscription(scope DocumentScope, collection UUID, predicate QueryPredicate, cursor string) (*Subscription, error) {
	if !validCursor(cursor) {
		return nil, fmt.Errorf("sdk: invalid subscription resume cursor")
	}
	s, err := d.Subscribe(scope, collection, predicate, 128)
	if err != nil {
		return nil, err
	}
	s.pollCursor = cursor
	return s, nil
}
func (s *Subscription) Next(ctx context.Context) (*SubscriptionBatch, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.pollCursor == "" {
		result, err := s.documents.SubscriptionSnapshot(ctx, s.scope, s.collection, SubscriptionSnapshotRequest{Version: 1, Phase: "snapshot", Predicate: s.predicate, Limit: s.limit, SnapshotCursor: s.snapshotCursor})
		if err != nil {
			return nil, err
		}
		if result.NextSnapshotCursor != "" && slices.Contains(s.seenSnapshotCursors, result.NextSnapshotCursor) {
			return nil, fmt.Errorf("sdk: repeated snapshot cursor")
		}
		if len(result.Rows) > 0 && result.Rows[0].Document <= s.lastDocument {
			return nil, fmt.Errorf("sdk: subscription snapshot documents did not advance across pages")
		}
		if len(result.Rows) > 0 {
			s.lastDocument = result.Rows[len(result.Rows)-1].Document
		}
		if result.NextSnapshotCursor != "" {
			s.seenSnapshotCursors = append(s.seenSnapshotCursors, result.NextSnapshotCursor)
			if len(s.seenSnapshotCursors) > 64 {
				s.seenSnapshotCursors = slices.Clone(s.seenSnapshotCursors[len(s.seenSnapshotCursors)-64:])
			}
		}
		s.snapshotCursor = result.NextSnapshotCursor
		s.pollCursor = result.PollCursor
		if s.pollCursor != "" {
			s.seenSnapshotCursors = nil
		}
		return &SubscriptionBatch{Snapshot: result}, nil
	}
	result, err := s.documents.SubscriptionPoll(ctx, s.scope, s.collection, SubscriptionPollRequest{Version: 1, Phase: "poll", Predicate: s.predicate, PollCursor: s.pollCursor})
	if err != nil {
		return nil, err
	}
	if len(result.Groups) > 0 && result.Groups[0].Sequence <= s.lastSequence {
		return nil, fmt.Errorf("sdk: subscription sequence did not advance")
	}
	if len(result.Groups) > 0 {
		s.lastSequence = result.Groups[len(result.Groups)-1].Sequence
	}
	s.pollCursor = result.PollCursor
	return &SubscriptionBatch{Poll: result}, nil
}
func (s *Subscription) PollCursor() string { s.mu.Lock(); defer s.mu.Unlock(); return s.pollCursor }
