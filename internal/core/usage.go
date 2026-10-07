package core

import (
	"context"
	"errors"
	"time"
)

// ErrNotMetered marks an organization, project or database TerraBase does
// not store, such as the dashboard's own phase-1 resources (TS-472), so it
// has no metered usage.
var ErrNotMetered = errors.New("usage is not metered for this resource")

// UsageMeter reads TerraBase's metered usage as the signed-in user, whose
// ts-auth access token is token.
type UsageMeter interface {
	Usage(ctx context.Context, token string, scope UsageScope) (Usage, error)
}

// UsageScope selects the organization, or one of its projects, or one of
// that project's databases, and the period: buckets of Granularity ("hour"
// or "day", UTC) from From until now.
type UsageScope struct {
	OrganizationID, ProjectID, DatabaseID string
	Granularity                           string
	From                                  time.Time
}

// UsageBuckets is the granularity and first bucket of a range ending at now:
// 24 or 168 hours for 24h and 7d (the default), or 30 days for 30d, the
// current hour or day included.
func UsageBuckets(rangeKey string, now time.Time) (string, time.Time) {
	now = now.UTC()
	switch rangeKey {
	case "24h":
		return "hour", now.Truncate(time.Hour).Add(-23 * time.Hour)
	case "30d":
		return "day", time.Date(now.Year(), now.Month(), now.Day()-29, 0, 0, 0, 0, time.UTC)
	default:
		return "hour", now.Truncate(time.Hour).Add(-167 * time.Hour)
	}
}

// Usage is TerraBase's usage of one organization, project or database. Reads
// and Writes are lifetime totals; Period counts the requested period and
// Series its buckets, oldest first. Items are the next level down: an
// organization's projects or a project's databases, without their series.
type Usage struct {
	OrganizationID string       `json:"organizationId,omitempty"`
	ProjectID      string       `json:"projectId,omitempty"`
	DatabaseID     string       `json:"databaseId,omitempty"`
	StorageBytes   int64        `json:"storageBytes"`
	LogicalBytes   int64        `json:"logicalBytes" jsonschema:"Metered logical storage: the bytes of the live documents."`
	Documents      int64        `json:"documents"`
	Reads          int64        `json:"reads"`
	Writes         int64        `json:"writes"`
	Period         UsagePeriod  `json:"period"`
	Series         []UsagePoint `json:"series"`
	Items          []Usage      `json:"items,omitempty"`
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

// UserToken is the request's ts-auth access token (WithUserToken), or empty
// without one, as for MCP and API callers.
func UserToken(ctx context.Context) string {
	token, _ := ctx.Value(userTokenKey{}).(func() (string, bool))
	if token == nil {
		return ""
	}
	access, _ := token()
	return access
}
