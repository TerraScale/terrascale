package v1

import (
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ShapeKey is the epoch key K_e for one shape handle, epoch and key ID. It lives
// in memory only. Each epoch needs its own ShapeKey.
type ShapeKey struct {
	Handle string
	Epoch  uint64
	KeyID  string
	// A function keeps reflection-based diagnostics from traversing key bytes.
	key func() []byte
}

// String and Format keep epoch-key bytes out of diagnostics for every verb.
func (ShapeKey) String() string               { return "terrabase shape key (key redacted)" }
func (k ShapeKey) Format(f fmt.State, _ rune) { fmt.Fprint(f, k.String()) }

// ShapeKey returns an epoch key cached in process memory by handle, epoch and
// key ID within this client's collection scope. Concurrent requests share a
// fetch. Past epochs and shape/key 409 responses evict cached keys. Network
// requests and responses are no-store; keys are never persisted.
func (col *Collection) ShapeKey(ctx context.Context, handle string, epoch uint64, keyID string) (*ShapeKey, error) {
	if !isShapeHandle(handle) {
		return nil, fmt.Errorf("terrabase: shape handle %q must be h1- and 16 lowercase hex digits", handle)
	}
	if err := checkID(keyID); err != nil {
		return nil, fmt.Errorf("terrabase: shape key ID: %w", err)
	}
	p, err := col.path("shape")
	if err != nil {
		return nil, err
	}
	id := shapeKeyIdentity{scope: strings.Join(p, "/"), handle: handle, epoch: epoch, keyID: keyID}
	return col.db.c.shapeKeys.get(ctx, id, func() (*ShapeKey, error) {
		return col.fetchShapeKey(ctx, append(p, "keys"), id)
	})
}

func (col *Collection) fetchShapeKey(ctx context.Context, p []string, id shapeKeyIdentity) (*ShapeKey, error) {
	handle, epoch, keyID := id.handle, id.epoch, id.keyID
	epochText := strconv.FormatUint(epoch, 10)
	req := request{
		method:     http.MethodGet,
		path:       p,
		query:      url.Values{"handle": {handle}, "epoch": {epochText}, "key_id": {keyID}},
		header:     http.Header{"Cache-Control": {"no-store"}},
		replaySafe: true,
	}
	resp, err := col.db.c.do(ctx, req)
	if resp != nil && resp.status == http.StatusConflict {
		col.db.c.shapeKeys.evict(id.scope)
	}
	if err != nil {
		return nil, err
	}
	if !hasNoStore(resp.header) {
		return nil, errors.New("terrabase: shape key response must be Cache-Control: no-store")
	}
	var body struct {
		Handle string `json:"handle"`
		Epoch  string `json:"epoch"`
		KeyID  string `json:"key_id"`
		Key    string `json:"key"`
	}
	if err := decodeExact(resp.body, &body); err != nil {
		return nil, fmt.Errorf("terrabase: decode shape key response: %w", err)
	}
	if body.Handle != handle || body.Epoch != epochText || body.KeyID != keyID {
		return nil, errors.New("terrabase: shape key response does not match the requested handle, epoch and key ID")
	}
	// Decode ignores CR and LF, so the re-encoded comparison is what rejects them.
	key, err := base64.RawURLEncoding.Strict().DecodeString(body.Key)
	if err != nil || len(key) != 32 || base64.RawURLEncoding.EncodeToString(key) != body.Key {
		return nil, errors.New("terrabase: shape key must be unpadded base64url for 32 bytes")
	}
	return &ShapeKey{Handle: handle, Epoch: epoch, KeyID: keyID, key: func() []byte { return key }}, nil
}

type shapeKeyIdentity struct {
	scope, handle, keyID string
	epoch                uint64
}

type shapeKeyEntry struct {
	ready          chan struct{}
	key            ShapeKey
	err            error
	ownerCancelled bool
}

// A fixed bound includes pending loads; saturation never starts extra work.
const maxShapeKeys = 256

type shapeKeyCache struct {
	mu      sync.Mutex
	entries map[shapeKeyIdentity]*shapeKeyEntry
	now     func() time.Time
}

func (cache *shapeKeyCache) timeNow() time.Time {
	if cache.now != nil {
		return cache.now()
	}
	return time.Now()
}

func (cache *shapeKeyCache) get(ctx context.Context, id shapeKeyIdentity, fetch func() (*ShapeKey, error)) (*ShapeKey, error) {
	for {
		key, retry, err := cache.getOnce(ctx, id, fetch)
		if err != nil && ctx.Err() != nil {
			return nil, ctx.Err()
		}
		// A live waiter retries with its own context when the fetch owner cancels.
		if retry {
			continue
		}
		return key, err
	}
}

func (cache *shapeKeyCache) getOnce(ctx context.Context, id shapeKeyIdentity, fetch func() (*ShapeKey, error)) (*ShapeKey, bool, error) {
	if err := ctx.Err(); err != nil {
		return nil, false, err
	}
	cache.mu.Lock()
	now := cache.timeNow()
	for key, entry := range cache.entries {
		if entry.key.key != nil && key.epoch < uint64(now.Unix()/600) {
			cache.remove(key, entry)
		}
	}
	if entry := cache.entries[id]; entry != nil {
		cache.mu.Unlock()
		select {
		case <-ctx.Done():
			return nil, false, ctx.Err()
		case <-entry.ready:
			if entry.err != nil {
				return nil, entry.ownerCancelled, entry.err
			}
			key := entry.key
			return &key, false, nil
		}
	}
	if len(cache.entries) >= maxShapeKeys {
		for oldID, old := range cache.entries {
			select {
			case <-old.ready:
				cache.remove(oldID, old)
			default:
			}
			if len(cache.entries) < maxShapeKeys {
				break
			}
		}
	}
	if len(cache.entries) >= maxShapeKeys {
		cache.mu.Unlock()
		return nil, false, ErrShapeBusy
	}
	if cache.entries == nil {
		cache.entries = make(map[shapeKeyIdentity]*shapeKeyEntry)
	}
	entry := &shapeKeyEntry{ready: make(chan struct{})}
	cache.entries[id] = entry
	cache.mu.Unlock()

	key, err := fetch()
	cache.mu.Lock()
	defer cache.mu.Unlock()
	// A 409 invalidates pending loads too; their late responses cannot restore keys.
	if cache.entries[id] != entry && err == nil {
		err = ErrMustRefetch
	}
	entry.err = err
	entry.ownerCancelled = ctx.Err() != nil && (errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded))
	if err == nil {
		entry.key = *key
		now = cache.timeNow()
		for oldID, old := range cache.entries {
			if oldID.scope == id.scope && oldID.handle == id.handle && oldID.epoch < id.epoch {
				cache.remove(oldID, old)
			}
		}
		// Expiry is checked on access; no background timer owns client state.
		if id.epoch != uint64(now.Unix()/600) {
			delete(cache.entries, id)
		}
	} else if cache.entries[id] == entry {
		delete(cache.entries, id)
	}
	close(entry.ready)
	if err != nil {
		return nil, false, err
	}
	copy := entry.key
	return &copy, false, nil
}

// remove is called with mu held. Returned caller-owned keys remain their responsibility.
func (cache *shapeKeyCache) remove(id shapeKeyIdentity, entry *shapeKeyEntry) {
	delete(cache.entries, id)
}

func (cache *shapeKeyCache) evict(scope string) {
	cache.mu.Lock()
	defer cache.mu.Unlock()
	for id, entry := range cache.entries {
		if id.scope == scope {
			cache.remove(id, entry)
		}
	}
}

// Decrypt opens one TBS1 object fetched at offset. Snapshot and boundary offsets
// are both valid object offsets; the bootstrap sentinel "-1" and invalid offsets are refused.
func (k *ShapeKey) Decrypt(offset string, ciphertext []byte) ([]byte, error) {
	if _, _, _, err := parseShapeOffset(offset); err != nil || offset == "-1" {
		return nil, errors.New("terrabase: shape object offset is invalid")
	}
	if k.key == nil {
		return nil, errors.New("terrabase: shape epoch key is missing")
	}
	return decryptShapeObject(ciphertext, k.key(), k.Handle, offset, k.Epoch, k.KeyID)
}

// hasNoStore reports whether any Cache-Control header carries the no-store directive.
func hasNoStore(h http.Header) bool {
	for _, value := range h.Values("Cache-Control") {
		for directive := range strings.SplitSeq(value, ",") {
			if strings.EqualFold(strings.TrimSpace(directive), "no-store") {
				return true
			}
		}
	}
	return false
}
