package v1

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"time"
)

// ShapeManifest lists presigned immutable mirror objects. Reading it does not
// fetch their URLs; callers must verify, decrypt, and apply objects separately.
type ShapeManifest struct {
	Handle  string              `json:"handle"`
	Objects []ShapeMirrorObject `json:"objects"`
}

// ShapeMirrorObject describes a sealed encrypted object and its integrity data.
// Epoch remains a decimal string, matching the producer's snake_case protocol.
type ShapeMirrorObject struct {
	Offset     string    `json:"offset"`
	NextOffset string    `json:"next_offset"`
	Binding    string    `json:"binding"`
	Object     string    `json:"object"`
	URL        string    `json:"url"`
	ExpiresAt  time.Time `json:"expires_at"`
	Epoch      string    `json:"epoch"`
	KeyID      string    `json:"key_id" ulid:"required"`
	SHA256     string    `json:"sha256"`
	Size       int64     `json:"size"`
}

// ShapeManifest reads at most eight mirror entries for a published offset.
// Empty entries mean use the origin shape route. Stale handles return ErrMustRefetch.
func (col *Collection) ShapeManifest(ctx context.Context, handle, offset string, where Filter) (*ShapeManifest, error) {
	if !isShapeHandle(handle) {
		return nil, errors.New("terrabase: invalid shape handle")
	}
	if _, _, _, err := parseShapeOffset(offset); err != nil || offset == "-1" {
		return nil, errors.New("terrabase: manifest requires a published shape offset")
	}
	p, err := col.path("shape", "manifest")
	if err != nil {
		return nil, err
	}
	q := url.Values{"handle": {handle}, "offset": {offset}}
	if !where.IsZero() {
		raw, err := json.Marshal(where)
		if err != nil {
			return nil, err
		}
		if len(raw) > 8<<10 {
			return nil, errors.New("terrabase: shape filter exceeds 8 KiB")
		}
		q.Set("where", string(raw))
	}
	var out ShapeManifest
	resp, err := col.db.c.call(ctx, request{method: http.MethodGet, path: p, query: q, header: http.Header{"Cache-Control": {"no-store"}}, replaySafe: true}, nil, &out)
	if err != nil {
		return nil, err
	}
	if !hasNoStore(resp.header) {
		return nil, errors.New("terrabase: shape manifest response must be Cache-Control: no-store")
	}
	if out.Handle != handle || out.Objects == nil || len(out.Objects) > 8 {
		return nil, errors.New("terrabase: invalid shape manifest response")
	}
	return &out, nil
}
