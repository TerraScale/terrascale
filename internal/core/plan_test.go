package core

import "testing"

func TestPlanAllowances(t *testing.T) {
	for _, test := range []struct {
		name string
		got  string
		want string
	}{
		{"storage label", Plan{StorageLabel: "5 GB-region storage", StorageMiB: 2048}.StorageAllowance(), "5 GB-region storage"},
		{"whole GiB", Plan{StorageMiB: 10240}.StorageAllowance(), "10 GiB storage"},
		{"MiB", Plan{StorageMiB: 512}.StorageAllowance(), "512 MiB storage"},
		{"no storage", Plan{}.StorageAllowance(), "Limit not published"},
		{"reads", Plan{OperationsPerMonth: 100_000_000, MonthlyRequests: 5}.ReadsAllowance(), "100M reads/month"},
		{"writes", Plan{OperationsPerMonth: 100_000_000, MonthlyRequests: 5}.WritesAllowance(), "100M writes/month"},
		{"thousand reads", Plan{OperationsPerMonth: 250_000}.ReadsAllowance(), "250K reads/month"},
		{"fractional millions", Plan{OperationsPerMonth: 1_500_000}.WritesAllowance(), "1.5M writes/month"},
		{"billions", Plan{OperationsPerMonth: 1_000_000_000}.ReadsAllowance(), "1B reads/month"},
		{"inexact count", Plan{OperationsPerMonth: 1_234_567}.ReadsAllowance(), "1234567 reads/month"},
		{"monthly requests", Plan{MonthlyRequests: 1_500}.WritesAllowance(), "1500 writes/month"},
		{"no reads limit", Plan{}.ReadsAllowance(), "Limit not published"},
		{"one database", Plan{MaxDatabases: 1}.DatabaseAllowance(), "1 database"},
		{"databases", Plan{MaxDatabases: 25}.DatabaseAllowance(), "25 databases"},
		{"one member", Plan{MaxMembers: 1}.MemberAllowance(), "1 team member"},
		{"members", Plan{MaxMembers: 5}.MemberAllowance(), "5 team members"},
		{"no member limit", Plan{}.MemberAllowance(), "Limit not published"},
	} {
		if test.got != test.want {
			t.Errorf("%s = %q, want %q", test.name, test.got, test.want)
		}
	}
}

func TestPlanLimitsForUtilization(t *testing.T) {
	plan := Plan{MonthlyRequests: 7, StorageMiB: 3}
	if plan.MonthlyOperations() != 7 || plan.StorageBytes() != 3<<20 {
		t.Fatalf("limits = %d operations, %d bytes", plan.MonthlyOperations(), plan.StorageBytes())
	}
	if huge := (Plan{StorageMiB: 1 << 50}); huge.StorageBytes() != MaxStorageMiB<<20 || huge.StorageBytes() <= 0 {
		t.Fatalf("storage of %d MiB = %d bytes, want it clamped, not wrapped", huge.StorageMiB, huge.StorageBytes())
	}
	if plan := (Plan{OperationsPerMonth: 9, MonthlyRequests: 7}); plan.MonthlyOperations() != 9 {
		t.Fatalf("OperationsPerMonth must win over MonthlyRequests, got %d", plan.MonthlyOperations())
	}
}
