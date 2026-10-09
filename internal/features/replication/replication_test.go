package replication

import (
	"context"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

type replicationClient struct{ result core.Replication }

func (c replicationClient) Replication(context.Context, string, core.MetricsScope) (core.Replication, error) {
	return c.result, nil
}
func (replicationClient) Analytics(context.Context, string, core.MetricsScope) (core.Analytics, error) {
	return core.Analytics{}, nil
}

func TestOrderedPairsAreNotDoubleBilledAndSparseBucketsAlign(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "replication@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	start := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	client := replicationClient{result: core.Replication{
		Regions: []core.ReplicationRegion{{Region: "gru", Status: "healthy", ReplicatedOps: 10, LagMS: new(float64(3))}, {Region: "sfo", Status: "lagging", ReplicatedOps: 5, LagMS: new(float64(321))}, {Region: "fra", Status: "diverged"}, {Region: "iad", Status: "stopped"}},
		Pairs: []core.ReplicationPair{
			{FromRegion: "sfo", ToRegion: "gru", SentBytes: 2048, ReceivedBytes: 2048, Series: []core.ReplicationPoint{{Start: start.Add(time.Minute), SentBytes: 2048}}},
			{FromRegion: "gru", ToRegion: "sfo", SentBytes: 4096, ReceivedBytes: 4096, Series: []core.ReplicationPoint{{Start: start.In(time.FixedZone("offset", 3600)), SentBytes: 4096}}},
			{DatabaseID: "01JC9Q9Y5N7P9R3T5V7X9Z2AC", FromRegion: "gru", ToRegion: "sfo", SentBytes: 1024, ReceivedBytes: 1024, Series: []core.ReplicationPoint{{Start: start, SentBytes: 1024}}},
		},
	}}
	v := Load(t.Context(), core.Deps{Store: store, Metrics: client}, account.Principal(), "", "", "1h")
	if v.SentBytes != 7168 || v.ReceivedBytes != 7168 || v.ReplicatedOps != 15 || v.WorstLagMS == nil || *v.WorstLagMS != 321 || v.Healthy != 1 || v.Lagging != 1 || v.Diverged != 1 || v.Stopped != 1 {
		t.Fatalf("summary=%+v", v)
	}
	if len(v.Directions) != 2 || v.Directions[0].FromRegion != "gru" || v.Directions[0].SentBytes != 5120 || v.Directions[1].SentBytes != 2048 {
		t.Fatalf("direction totals=%+v", v.Directions)
	}
	if len(v.Chart.Paths) != 2 || len(v.Chart.Columns) != 2 {
		t.Fatalf("chart=%+v", v.Chart)
	}
	for _, label := range []string{"gru → sfo sent bytes 5,120", "sfo → gru sent bytes 0"} {
		if !strings.Contains(v.Chart.Columns[0].Label, label) {
			t.Fatalf("first bucket=%q", v.Chart.Columns[0].Label)
		}
	}
}
