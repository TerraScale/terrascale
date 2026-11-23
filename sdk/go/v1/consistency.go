package v1

import (
	"context"
	"net/http"

	"terrascale.tech/sdk/internal/causal"
)

type Consistency = causal.Consistency

const (
	ConsistencyFast   Consistency = causal.Fast
	ConsistencyStrong Consistency = causal.Strong
	BookmarkHeader                = causal.BookmarkHeader
	ConsistencyHeader             = causal.ConsistencyHeader
)

var (
	ErrInvalidBookmarkValue    = causal.ErrInvalidBookmark
	ErrInvalidConsistencyValue = causal.ErrInvalidConsistency
)

func ParseConsistency(value string) (Consistency, error) { return causal.ParseConsistency(value) }

func WithBookmark(ctx context.Context, bookmark string) context.Context {
	return causal.WithBookmark(ctx, bookmark)
}

func WithConsistency(ctx context.Context, mode Consistency) context.Context {
	return causal.WithConsistency(ctx, mode)
}

func MergeBookmarks(bookmarks ...string) (string, error) { return causal.Merge(bookmarks...) }

func (d *Database) Bookmark() string { return d.causal.Load() }

func (d *Database) ObserveBookmark(bookmark string) error { return d.causal.Observe(bookmark) }

func (s *SQLSession) Bookmark() string { return s.d.causal.Load() }

func (s *SQLSession) BeginStrong(ctx context.Context) error {
	return s.Begin(WithConsistency(ctx, ConsistencyStrong))
}

func causalHeader(ctx context.Context, tracker *causal.Tracker) (http.Header, error) {
	bookmark, err := tracker.Outgoing(ctx)
	if err != nil {
		return nil, err
	}
	mode, err := causal.ConsistencyFrom(ctx)
	if err != nil {
		return nil, err
	}
	header := http.Header{}
	if bookmark != "" {
		header.Set(BookmarkHeader, bookmark)
	}
	if mode != "" {
		header.Set(ConsistencyHeader, string(mode))
	}
	return header, nil
}
