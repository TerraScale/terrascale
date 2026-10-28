package v1

import (
	"context"
	"errors"
	"net/http"
	"net/url"
)

// IndexRecommendation is a workload-derived index suggestion. Its ID is an
// opaque producer identifier, not an entity ULID.
type IndexRecommendation struct {
	ID         string   `json:"id"`
	Collection string   `json:"collection"`
	Fields     []string `json:"fields"`
	Reason     string   `json:"reason"`
	Queries    int64    `json:"queries"`
}

// AppliedIndexRecommendation reports applied or already-applied.
type AppliedIndexRecommendation struct {
	Status string `json:"status"`
	Index  Index  `json:"index"`
}

// CreateIndex creates a secondary index; vector builds may return state building.
func (d *Database) CreateIndex(ctx context.Context, in IndexRequest) (*Index, error) {
	if err := in.validate(); err != nil {
		return nil, err
	}
	return databaseCall[Index](ctx, d, http.MethodPost, nil, in, "indexes")
}

// IndexListOptions filters indexes by collection; empty lists all indexes.
type IndexListOptions struct{ Collection string }

// Indexes lists secondary indexes, optionally restricted to one collection.
func (d *Database) Indexes(ctx context.Context, options ...IndexListOptions) ([]Index, error) {
	if len(options) > 1 {
		return nil, errors.New("terrabase: Indexes accepts at most one options value")
	}
	q := url.Values{}
	if len(options) == 1 && options[0].Collection != "" {
		if err := checkName("collection", options[0].Collection); err != nil {
			return nil, err
		}
		q.Set("collection", options[0].Collection)
	}
	out, err := databaseCall[struct {
		Items []Index `json:"items"`
	}](ctx, d, http.MethodGet, q, nil, "indexes")
	if err != nil {
		return nil, err
	}
	return out.Items, nil
}

// DropIndex removes a named secondary index.
func (d *Database) DropIndex(ctx context.Context, name string) error {
	if err := checkName("index", name); err != nil {
		return err
	}
	p, err := d.path("indexes", name)
	if err != nil {
		return err
	}
	_, err = d.c.call(ctx, request{method: http.MethodDelete, path: p}, nil, nil)
	return err
}

// IndexRecommendations lists workload-derived recommendations.
func (d *Database) IndexRecommendations(ctx context.Context) ([]IndexRecommendation, error) {
	out, err := databaseCall[struct {
		Items []IndexRecommendation `json:"items"`
	}](ctx, d, http.MethodGet, nil, nil, "index-recommendations")
	if err != nil {
		return nil, err
	}
	return out.Items, nil
}

// ApplyIndexRecommendation creates the suggested index, or returns the
// existing index if it has already been applied.
func (d *Database) ApplyIndexRecommendation(ctx context.Context, id string) (*AppliedIndexRecommendation, error) {
	return databaseCall[AppliedIndexRecommendation](ctx, d, http.MethodPost, nil, nil, "index-recommendations", id, "apply")
}

func (in IndexRequest) validate() error {
	if err := checkName("collection", in.Collection); err != nil {
		return err
	}
	if in.Kind != "" && in.Kind != IndexBTree && in.Kind != IndexText && in.Kind != IndexVector {
		return errors.New("terrabase: index kind must be btree, text or vector")
	}
	if in.Kind == IndexVector {
		if len(in.Fields) != 1 || in.Dimensions == nil || *in.Dimensions < 1 || *in.Dimensions > 4096 {
			return errors.New("terrabase: a vector index has one field and dimensions 1-4096")
		}
	} else if in.Dimensions != nil {
		return errors.New("terrabase: only vector indexes take dimensions")
	}
	return nil
}
