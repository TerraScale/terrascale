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
)

// ShapeKey is the epoch key K_e for one shape handle, epoch and key ID. It lives
// in memory only. Each epoch needs its own ShapeKey; nothing is cached across epochs.
type ShapeKey struct {
	Handle string
	Epoch  uint64
	KeyID  string
	key    []byte
}

// ShapeKey fetches the epoch key for handle at epoch, named by keyID. The request
// and the response are both no-store, and the response must echo the requested
// handle, epoch and key ID before the key is trusted.
func (col *Collection) ShapeKey(ctx context.Context, handle string, epoch uint64, keyID string) (*ShapeKey, error) {
	if !isShapeHandle(handle) {
		return nil, fmt.Errorf("terrabase: shape handle %q must be h1- and 16 lowercase hex digits", handle)
	}
	if err := checkID(keyID); err != nil {
		return nil, fmt.Errorf("terrabase: shape key ID: %w", err)
	}
	p, err := col.path("shape", "keys")
	if err != nil {
		return nil, err
	}
	epochText := strconv.FormatUint(epoch, 10)
	req := request{
		method:     http.MethodGet,
		path:       p,
		query:      url.Values{"handle": {handle}, "epoch": {epochText}, "key_id": {keyID}},
		header:     http.Header{"Cache-Control": {"no-store"}},
		replaySafe: true,
	}
	resp, err := col.db.c.do(ctx, req)
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
	return &ShapeKey{Handle: handle, Epoch: epoch, KeyID: keyID, key: key}, nil
}

// Decrypt opens one TBS1 object fetched at offset. Snapshot and boundary offsets
// are both valid object offsets; the bootstrap sentinel "-1" and invalid offsets are refused.
func (k *ShapeKey) Decrypt(offset string, ciphertext []byte) ([]byte, error) {
	if _, _, _, err := parseShapeOffset(offset); err != nil || offset == "-1" {
		return nil, errors.New("terrabase: shape object offset is invalid")
	}
	return decryptShapeObject(ciphertext, k.key, k.Handle, offset, k.Epoch, k.KeyID)
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
