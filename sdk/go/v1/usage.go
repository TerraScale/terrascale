package v1

import (
	"context"
	"errors"
	"net/http"
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
	ProjectID    string       `json:"projectId,omitempty" ulid:"optional"`
	DatabaseID   string       `json:"databaseId,omitempty" ulid:"optional"`
	Databases    int64        `json:"databases"`
	Collections  int64        `json:"collections"`
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
	query, err := opts.query()
	if err != nil {
		return nil, err
	}
	path := projectPath(org, project, "usage")
	if database != "" {
		path = databasePath(org, project, database, "usage")
	}
	return get[Usage](ctx, u, path, query)
}

func (opts UsageOptions) query() (url.Values, error) {
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
	return query, nil
}

// ProjectUsage reads project usage and its database items using a project access
// token with databases:read. Counters and window aggregation come from the server.
func (c *Client) ProjectUsage(ctx context.Context, project string, opts UsageOptions) (*Usage, error) {
	if err := checkID(project); err != nil {
		return nil, err
	}
	return c.usage(ctx, []string{"v1", "projects", project, "usage"}, opts)
}

// Usage reads this database's metered usage by name using databases:read.
func (d *Database) Usage(ctx context.Context, opts UsageOptions) (*Usage, error) {
	path, err := d.path("usage")
	if err != nil {
		return nil, err
	}
	return d.c.usage(ctx, path, opts)
}

func (c *Client) usage(ctx context.Context, path []string, opts UsageOptions) (*Usage, error) {
	query, err := opts.query()
	if err != nil {
		return nil, err
	}
	var out struct {
		Usage
		StorageBytes *int64 `json:"storageBytes"`
		Documents    *int64 `json:"documents"`
		Reads        *int64 `json:"reads"`
		Writes       *int64 `json:"writes"`
	}
	_, err = c.call(ctx, request{method: http.MethodGet, path: path, query: query, replaySafe: true}, nil, &out)
	if err != nil {
		return nil, err
	}
	if out.StorageBytes == nil || out.Documents == nil || out.Reads == nil || out.Writes == nil {
		return nil, errors.New("terrabase: usage response requires storageBytes, documents, reads and writes counters")
	}
	out.Usage.StorageBytes, out.Usage.Documents = *out.StorageBytes, *out.Documents
	out.Usage.Reads, out.Usage.Writes = *out.Reads, *out.Writes
	return &out.Usage, nil
}
