package causal

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"maps"
	"slices"
	"strings"
	"sync"
	"unicode/utf8"
)

type Consistency string

const (
	Fast   Consistency = "fast"
	Strong Consistency = "strong"
)

const (
	BookmarkHeader    = "TerraScale-Bookmark"
	ConsistencyHeader = "TerraScale-Consistency"
	MaxBookmarkLength = 1024
	maxRegionBytes    = 64
	formatVersion     = 1
)

var (
	ErrInvalidBookmark    = errors.New("terrascale: bookmark must be one TerraScale-Bookmark value returned by TerraScale")
	ErrInvalidConsistency = errors.New("terrascale: consistency must be fast or strong")
)

func (c Consistency) Valid() bool { return c == Fast || c == Strong }

func ParseConsistency(value string) (Consistency, error) {
	mode := Consistency(strings.ToLower(strings.TrimSpace(value)))
	if !mode.Valid() {
		return "", ErrInvalidConsistency
	}
	return mode, nil
}

type entry struct {
	region   string
	sequence uint64
}

func decode(bookmark string) ([]entry, error) {
	if bookmark == "" {
		return nil, nil
	}
	if len(bookmark) > MaxBookmarkLength {
		return nil, ErrInvalidBookmark
	}
	raw, err := base64.RawURLEncoding.DecodeString(bookmark)
	if err != nil || len(raw) == 0 || raw[0] != formatVersion {
		return nil, ErrInvalidBookmark
	}
	cursor := raw[1:]
	count, n := binary.Uvarint(cursor)
	if n <= 0 || count > uint64(len(cursor)) {
		return nil, ErrInvalidBookmark
	}
	cursor = cursor[n:]
	entries := make([]entry, 0, count)
	for range count {
		length, n := binary.Uvarint(cursor)
		if n <= 0 || length == 0 || length > maxRegionBytes || length > uint64(len(cursor)-n) {
			return nil, ErrInvalidBookmark
		}
		cursor = cursor[n:]
		region := cursor[:length]
		if !utf8.Valid(region) {
			return nil, ErrInvalidBookmark
		}
		cursor = cursor[length:]
		sequence, n := binary.Uvarint(cursor)
		if n <= 0 || sequence == 0 || sequence > 1<<63-1 {
			return nil, ErrInvalidBookmark
		}
		cursor = cursor[n:]
		if len(entries) > 0 && entries[len(entries)-1].region >= string(region) {
			return nil, ErrInvalidBookmark
		}
		entries = append(entries, entry{region: string(region), sequence: sequence})
	}
	if len(cursor) != 0 {
		return nil, ErrInvalidBookmark
	}
	return entries, nil
}

func encode(sequences map[string]uint64) string {
	if len(sequences) == 0 {
		return ""
	}
	out := []byte{formatVersion}
	out = binary.AppendUvarint(out, uint64(len(sequences)))
	for _, region := range slices.Sorted(maps.Keys(sequences)) {
		out = binary.AppendUvarint(out, uint64(len(region)))
		out = append(out, region...)
		out = binary.AppendUvarint(out, sequences[region])
	}
	return base64.RawURLEncoding.EncodeToString(out)
}

func Valid(bookmark string) bool {
	_, err := decode(bookmark)
	return err == nil
}

func Merge(bookmarks ...string) (string, error) {
	newest := map[string]uint64{}
	for _, bookmark := range bookmarks {
		entries, err := decode(bookmark)
		if err != nil {
			return "", err
		}
		for _, e := range entries {
			newest[e.region] = max(newest[e.region], e.sequence)
		}
	}
	merged := encode(newest)
	if len(merged) > MaxBookmarkLength {
		return "", ErrInvalidBookmark
	}
	return merged, nil
}

type bookmarkKey struct{}

type consistencyKey struct{}

func WithBookmark(ctx context.Context, bookmark string) context.Context {
	return context.WithValue(ctx, bookmarkKey{}, bookmark)
}

func BookmarkFrom(ctx context.Context) string {
	bookmark, _ := ctx.Value(bookmarkKey{}).(string)
	return bookmark
}

func WithConsistency(ctx context.Context, mode Consistency) context.Context {
	return context.WithValue(ctx, consistencyKey{}, mode)
}

func ConsistencyFrom(ctx context.Context) (Consistency, error) {
	mode, ok := ctx.Value(consistencyKey{}).(Consistency)
	if !ok {
		return "", nil
	}
	if !mode.Valid() {
		return "", ErrInvalidConsistency
	}
	return mode, nil
}

type Tracker struct {
	mu       sync.Mutex
	bookmark string
}

func (t *Tracker) Load() string {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.bookmark
}

func (t *Tracker) Observe(bookmark string) error {
	if bookmark == "" {
		return nil
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	merged, err := Merge(t.bookmark, bookmark)
	if err != nil {
		return err
	}
	t.bookmark = merged
	return nil
}

func (t *Tracker) Outgoing(ctx context.Context) (string, error) {
	requested := BookmarkFrom(ctx)
	if requested == "" {
		return t.Load(), nil
	}
	return Merge(t.Load(), requested)
}
