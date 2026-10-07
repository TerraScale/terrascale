package v1

import (
	"context"
	"errors"
	"net/url"
	"time"
)

// UsageOptions selects UTC hour or day buckets. Zero values use TerraBase's
// default window. From is inclusive; To is exclusive.
type UsageOptions struct {
	Granularity string
	From, To    time.Time
}

// Usage contains lifetime counters, logical storage and the requested window.
// Period is nil on older servers that report lifetime counters only; callers
// must never treat those counters as usage of the requested window.
type Usage struct {
	ProjectID    string       `json:"projectId,omitempty"`
	DatabaseID   string       `json:"databaseId,omitempty"`
	StorageBytes int64        `json:"storageBytes"`
	LogicalBytes int64        `json:"logicalBytes"`
	Documents    int64        `json:"documents"`
	Reads        int64        `json:"reads"`
	Writes       int64        `json:"writes"`
	Period       *UsagePeriod `json:"period"`
	Series       []UsagePoint `json:"series"`
	Items        []Usage      `json:"items,omitempty"`
}

type UsagePeriod struct {
	From        time.Time `json:"from"`
	To          time.Time `json:"to"`
	Granularity string    `json:"granularity"`
	Reads       int64     `json:"reads"`
	Writes      int64     `json:"writes"`
}

type UsagePoint struct {
	Start  time.Time `json:"start"`
	Reads  int64     `json:"reads"`
	Writes int64     `json:"writes"`
}

// Usage reads one project's or database's metered usage. An empty database
// selects the project and returns its databases in Items. TerraBase owns no
// organizations: clients sum the projects owned by an organization in ts-auth.
func (u *ManagementUser) Usage(ctx context.Context, org, project, database string, opts UsageOptions) (*Usage, error) {
	if project == "" {
		return nil, errors.New("terrabase: usage requires a project")
	}
	if opts.Granularity != "" && opts.Granularity != "hour" && opts.Granularity != "day" {
		return nil, errors.New("terrabase: usage granularity must be hour or day")
	}
	if !opts.From.IsZero() && !opts.To.IsZero() && !opts.From.Before(opts.To) {
		return nil, errors.New("terrabase: usage From must precede To")
	}
	query := url.Values{}
	if opts.Granularity != "" {
		query.Set("granularity", opts.Granularity)
	}
	if !opts.From.IsZero() {
		query.Set("from", opts.From.UTC().Format(time.RFC3339Nano))
	}
	if !opts.To.IsZero() {
		query.Set("to", opts.To.UTC().Format(time.RFC3339Nano))
	}
	path := projectPath(org, project, "usage")
	if database != "" {
		path = databasePath(org, project, database, "usage")
	}
	return get[Usage](ctx, u, path, query)
}
