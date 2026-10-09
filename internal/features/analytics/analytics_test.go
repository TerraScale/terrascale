package analytics

import (
	"context"
	"errors"
	"slices"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

type scopeClient struct {
	scopes []core.MetricsScope
	token  string
}

func (c *scopeClient) Analytics(_ context.Context, token string, scope core.MetricsScope) (core.Analytics, error) {
	c.token = token
	c.scopes = append(c.scopes, scope)
	return core.Analytics{}, nil
}
func (*scopeClient) Replication(context.Context, string, core.MetricsScope) (core.Replication, error) {
	return core.Replication{}, nil
}

func TestScopeAuthorizationAndProviderIDs(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Email: "analytics@example.test", Name: "Owner", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	const databaseID = "01JC9Q8Y5N7P9R3T5V7X9Z2AC"
	const otherID = "01JC9QA15N7P9R3T5V7X9Z2AC"
	err = store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		org.PlanID = core.StarterPlanID
		project := org.Projects[p.ProjectID]
		project.Databases[databaseID] = core.Database{ID: databaseID, Name: "primary", Region: "GRU", Regions: []string{"GRU", "SFO"}}
		project.Databases[otherID] = core.Database{ID: otherID, Name: "other", Region: "FRA"}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	client := &scopeClient{}
	deps := core.Deps{Store: store, Metrics: client}
	ctx := core.WithUserToken(t.Context(), func() (string, bool) { return "user-access-token", true })
	for _, rangeKey := range []string{"1h", "24h", "7d", "30d"} {
		_, err := Load(ctx, deps, p, "", databaseID, "SFO", rangeKey)
		if err != nil {
			t.Fatal(err)
		}
		got := client.scopes[len(client.scopes)-1]
		if got.OrganizationID != p.OrganizationID || got.ProjectID != p.ProjectID || got.DatabaseID != databaseID || got.Region != "SFO" || got.Range != rangeKey || client.token != "user-access-token" {
			t.Fatalf("scope=%+v", got)
		}
	}
	view, err := Load(ctx, deps, p, "", otherID, "SFO", "24h")
	if err != nil || view.Region != "" || !slices.Equal(view.Regions, []string{"FRA"}) || client.scopes[len(client.scopes)-1].Region != "" {
		t.Fatalf("stale region: view=%+v err=%v", view, err)
	}
	view, err = Load(ctx, deps, p, "", databaseID, "GRU", "24h")
	if err != nil || view.Region != "GRU" || !slices.Equal(view.Regions, []string{"GRU", "SFO"}) {
		t.Fatalf("selected regions: view=%+v err=%v", view, err)
	}
	view, err = Load(ctx, deps, p, "", "", "FRA", "24h")
	if err != nil || view.Region != "FRA" || !slices.Equal(view.Regions, []string{"FRA", "GRU", "SFO"}) {
		t.Fatalf("project regions: view=%+v err=%v", view, err)
	}
	before := len(client.scopes)
	for _, filter := range []struct{ db, region string }{{"01JC9QC15N7P9R3T5V7X9Z2AC", ""}, {databaseID, "LHR"}} {
		if _, err := Load(ctx, deps, p, "", filter.db, filter.region, "24h"); err == nil {
			t.Fatal("invalid scope accepted")
		}
	}
	outsider := p
	outsider.UserID = "01JC9QB15N7P9R3T5V7X9Z2AC"
	if _, err := Load(ctx, deps, outsider, "", databaseID, "", "24h"); !errors.Is(err, core.ErrNotFound) && !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("outsider error=%v", err)
	}
	if len(client.scopes) != before {
		t.Fatal("unauthorized filter reached the provider")
	}
}

func TestBucketCountsAndLatestGauges(t *testing.T) {
	start := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	points := []core.AnalyticsPoint{
		{Start: start.Add(time.Minute), Requests: map[string]int64{"query": 4}, Operations: map[string]int64{"put": 1}, Errors: 2, P95LatencyMS: new(1.75), StorageBytes: 2048, ClientSentBytes: 3, ClientReceivedBytes: 4, ReplicationBytes: 5},
		{Start: start, Requests: map[string]int64{"query": 2, "write": 3}, Operations: map[string]int64{"get": 6}, Errors: 1, P95LatencyMS: new(9.0), StorageBytes: 8192, ClientSentBytes: 7, ClientReceivedBytes: 8, ReplicationBytes: 9},
		{Start: start.Add(time.Minute), DatabaseID: "01JC9Q9Y5N7P9R3T5V7X9Z2AC", Region: "SFO", Requests: map[string]int64{"query": 3}, StorageBytes: 2048},
	}
	v := View{Range: "1h"}
	build(&v, points)
	if v.Requests != 12 || v.Operations != 7 || v.Errors != 3 || v.StorageBytes != 4096 || v.LatestP95MS == nil || *v.LatestP95MS != 1.75 || v.ClientBytes != 22 || v.ReplicationBytes != 14 {
		t.Fatalf("totals=%+v", v)
	}
	if len(v.Charts) != 7 || len(v.Charts[0].Data.Paths) != 2 || len(v.Charts[1].Data.Paths) != 2 || len(v.Charts[0].Data.Columns) != 2 {
		t.Fatalf("charts=%+v", v.Charts)
	}
	if !points[0].Start.Equal(start.Add(time.Minute)) {
		t.Fatal("provider series mutated")
	}
	if latency(v.LatestP95MS) != "1.750 ms" {
		t.Fatal("sub-millisecond precision lost")
	}
}

func TestMissingLatencyIsUnknownAndNeverPlottedAsZero(t *testing.T) {
	start := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	v := View{Range: "24h"}
	build(&v, []core.AnalyticsPoint{{Start: start, P95LatencyMS: new(0.0)}, {Start: start.Add(time.Minute)}})
	if v.LatestP95MS != nil || latency(v.LatestP95MS) != "Not reported" {
		t.Fatal("latest missing latency replaced with a measurement")
	}
	chart := v.Charts[3].Data
	if len(chart.Columns) != 2 || len(chart.Columns[0].Dots) != 1 || len(chart.Columns[1].Dots) != 0 || !strings.Contains(chart.Columns[1].Label, "Not reported") {
		t.Fatalf("missing latency chart=%+v", chart)
	}
}
