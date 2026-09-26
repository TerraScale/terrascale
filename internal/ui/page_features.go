package ui

import "context"

// PageFeatures records the optional browser features a page used while it
// rendered, so the layout loads their scripts and the renderer widens the
// Content-Security-Policy only on pages that need it.
type PageFeatures struct {
	CodeEditor bool
}

type pageFeaturesKey struct{}

// WithPageFeatures returns a context whose render records into features.
func WithPageFeatures(ctx context.Context) (context.Context, *PageFeatures) {
	features := &PageFeatures{}
	return context.WithValue(ctx, pageFeaturesKey{}, features), features
}

func pageFeaturesFrom(ctx context.Context) *PageFeatures {
	features, _ := ctx.Value(pageFeaturesKey{}).(*PageFeatures)
	return features
}
