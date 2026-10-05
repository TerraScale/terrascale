package throttle

import (
	"context"
	"fmt"
	"testing"
	"time"
)

func TestMemoryStoreBatchCapacity(t *testing.T) {
	client := Bucket{Key: "client new", Budget: Budget{Burst: 3, Interval: time.Minute}}
	account := Bucket{Key: "account new", Budget: Budget{Burst: 3, Interval: 2 * time.Minute}}
	tracked := Bucket{Key: "tracked 0", Budget: Budget{Burst: 3, Interval: time.Minute}}
	spent := Bucket{Key: tracked.Key, Budget: Budget{Burst: 1, Interval: time.Minute}}
	for _, tc := range []struct {
		name       string
		count      int
		refilled   int
		spent      bool
		prunedAt   time.Time
		buckets    []Bucket
		wantWait   time.Duration
		wantOK     bool
		wantCount  int
		wantCharge map[string]int
	}{
		{
			name: "two new keys exceed the last slot", count: maxKeys - 1,
			buckets: []Bucket{client, account}, wantWait: 2 * time.Minute, wantCount: maxKeys - 1,
		},
		{
			name: "capacity refusal preserves tracked budgets", count: maxKeys - 1,
			buckets: []Bucket{tracked, client, account}, wantWait: 2 * time.Minute, wantCount: maxKeys - 1,
		},
		{
			name: "larger batch exceeds capacity", count: maxKeys - 1,
			buckets:  []Bucket{client, account, {Key: "third", Budget: client.Budget}},
			wantWait: 2 * time.Minute, wantCount: maxKeys - 1,
		},
		{
			name: "exact fit charges all budgets", count: maxKeys - 2,
			buckets: []Bucket{tracked, client, account}, wantOK: true, wantCount: maxKeys,
			wantCharge: map[string]int{tracked.Key: 1, client.Key: 1, account.Key: 1},
		},
		{
			name: "duplicate new key uses one slot and charges twice", count: maxKeys - 1,
			buckets: []Bucket{client, client}, wantOK: true, wantCount: maxKeys,
			wantCharge: map[string]int{client.Key: 2},
		},
		{
			name: "full store still charges tracked duplicates", count: maxKeys,
			buckets: []Bucket{tracked, tracked}, wantOK: true, wantCount: maxKeys,
			wantCharge: map[string]int{tracked.Key: 2},
		},
		{
			name: "refilled pruning recovers room for a batch", count: maxKeys - 1, refilled: maxKeys - 1,
			buckets: []Bucket{tracked, client, account}, wantOK: true, wantCount: 3,
			wantCharge: map[string]int{tracked.Key: 1, client.Key: 1, account.Key: 1},
		},
		{
			name: "prune scan stays bounded", count: maxKeys - 1, refilled: maxKeys - 1,
			prunedAt: start.Add(-pruneEvery / 2), buckets: []Bucket{tracked, client, account},
			wantWait: 2 * time.Minute, wantCount: maxKeys - 1,
		},
		{
			name: "rate refusal preserves longest wait", count: maxKeys - 2, spent: true,
			buckets: []Bucket{client, spent}, wantWait: 3 * time.Minute, wantCount: maxKeys - 2,
		},
		{
			name: "capacity and rate refusal preserve longest wait", count: maxKeys - 1, spent: true,
			buckets: []Bucket{spent, client, account}, wantWait: 3 * time.Minute, wantCount: maxKeys - 1,
		},
		{
			name: "partial pruning permits exact fit", count: maxKeys - 1, refilled: 1,
			buckets: []Bucket{client, account}, wantOK: true, wantCount: maxKeys,
			wantCharge: map[string]int{client.Key: 1, account.Key: 1},
		},
		{
			name: "pruned tracked keys also need a slot", count: maxKeys, refilled: 2,
			buckets: []Bucket{tracked, client, account}, wantWait: 2 * time.Minute, wantCount: maxKeys - 2,
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store := NewMemoryStore()
			store.prunedAt = tc.prunedAt
			for i := range tc.count {
				due := start.Add(time.Minute)
				if i < tc.refilled {
					due = start
				}
				store.due[fmt.Sprintf("tracked %d", i)] = due
			}
			if tc.spent {
				store.due[tracked.Key] = start.Add(3 * time.Minute)
			}
			before := store.due[tracked.Key]
			wait, ok, err := store.Reserve(context.Background(), start, tc.buckets)
			if err != nil || wait != tc.wantWait || ok != tc.wantOK {
				t.Errorf("Reserve = %v, %t, %v; want %v, %t, nil", wait, ok, err, tc.wantWait, tc.wantOK)
			}
			if len(store.due) != tc.wantCount {
				t.Errorf("tracking %d keys; want %d", len(store.due), tc.wantCount)
			}
			for _, bucket := range tc.buckets {
				base, exists := start, false
				if bucket.Key == tracked.Key {
					base, exists = before, true
					if store.prunedAt == start && !base.After(start) {
						exists = false
					}
				}
				charge := tc.wantCharge[bucket.Key]
				got, found := store.due[bucket.Key]
				if charge > 0 {
					exists = true
					base = base.Add(time.Duration(charge) * bucket.Budget.Interval)
				}
				if found != exists || (exists && !got.Equal(base)) {
					t.Errorf("bucket %q = %v, %t; want %v, %t", bucket.Key, got, found, base, exists)
				}
			}
			if !tc.prunedAt.IsZero() {
				if store.prunedAt != tc.prunedAt {
					t.Errorf("prune time = %v; want unchanged %v", store.prunedAt, tc.prunedAt)
				}
				wait, ok, err = store.Reserve(context.Background(), start.Add(pruneEvery/2), tc.buckets)
				if err != nil || wait != 0 || !ok || len(store.due) != 3 {
					t.Errorf("Reserve after prune interval = %v, %t, %v with %d keys; want 0, true, nil with 3 keys",
						wait, ok, err, len(store.due))
				}
			}
		})
	}
}
