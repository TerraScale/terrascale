package demovisuals

import (
	"slices"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestSampleWorkloadIsDeterministicAndScopedToKnownFixture(t *testing.T) {
	org := core.Organization{ID: MarioOrganizationID, Slug: "terrascale-labs"}
	account := core.Account{User: core.User{ID: MarioUserID}, ActiveOrganizationID: org.ID}
	if !IsSeededMarioAccount(true, account, org) || IsSeededMarioAccount(false, account, org) {
		t.Fatal("seeded fixture must require demo mode")
	}
	member := core.Account{User: core.User{ID: "member"}, ActiveOrganizationID: org.ID}
	if IsSeededMarioAccount(true, member, org) ||
		IsSeededMarioAccount(true, account, core.Organization{ID: MarioOrganizationID, Slug: "other"}) ||
		IsSeededMarioAccount(true, account, core.Organization{ID: "other", Slug: org.Slug}) {
		t.Fatal("sample visualization escaped its seeded organization")
	}
	for _, test := range []struct {
		rangeName string
		count     int
	}{
		{rangeName: "24h", count: 24},
		{rangeName: "7d", count: 168},
		{rangeName: "30d", count: 30},
	} {
		points := Workload(test.rangeName)
		if len(points) != test.count || !slices.Equal(points, Workload(test.rangeName)) || Sum(points) <= 0 {
			t.Errorf("%s sample series changed or is empty", test.rangeName)
		}
		reads, writes, replication := Operations(points)
		if len(reads) != test.count || len(writes) != test.count || len(replication) != test.count {
			t.Fatalf("%s operation series have wrong lengths", test.rangeName)
		}
		for i, total := range points {
			if reads[i]+writes[i] != total || replication[i] < 0 {
				t.Fatalf("%s point %d lost its operation split", test.rangeName, i)
			}
		}
		if test.rangeName == "7d" && (Sum(reads) != 32638 || Sum(writes) != 19724 || Sum(replication) != 6908) {
			t.Fatalf("source demo's 7d chart totals changed: reads=%d writes=%d replication=%d", Sum(reads), Sum(writes), Sum(replication))
		}
	}
}

func TestPointLabelsNameEachBucketFromNow(t *testing.T) {
	for _, tc := range []struct {
		value       string
		count       int
		first, last string
	}{
		{"24h", 24, "23h ago", "Now"},
		{"7d", 168, "6d 23h ago", "Now"},
		{"7d", 7, "6d ago", "Now"},
		{"30d", 30, "29d ago", "Now"},
	} {
		labels := PointLabels(tc.value, tc.count)
		if len(labels) != tc.count || labels[0] != tc.first || labels[len(labels)-1] != tc.last {
			t.Fatalf("PointLabels(%q, %d) = %q … %q, want %q … %q", tc.value, tc.count, labels[0], labels[len(labels)-1], tc.first, tc.last)
		}
	}
	if labels := PointLabels("7d", 168); labels[166] != "1h ago" {
		t.Fatalf("second to last hourly label = %q", labels[166])
	}
}
