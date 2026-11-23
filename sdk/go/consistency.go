package sdk

import (
	"context"
	"errors"

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
	ErrInvalidBookmark       = causal.ErrInvalidBookmark
	ErrInvalidConsistency    = causal.ErrInvalidConsistency
	ErrSerializationConflict = errors.New("terrascale: serialization_conflict: the transaction conflicted with a concurrent one and was rolled back; retry it")
)

func ParseConsistency(value string) (Consistency, error) { return causal.ParseConsistency(value) }

func WithBookmark(ctx context.Context, bookmark string) context.Context {
	return causal.WithBookmark(ctx, bookmark)
}

func WithConsistency(ctx context.Context, mode Consistency) context.Context {
	return causal.WithConsistency(ctx, mode)
}

func MergeBookmarks(bookmarks ...string) (string, error) { return causal.Merge(bookmarks...) }
