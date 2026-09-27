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
		{"operations", Plan{OperationsPerMonth: 100_000_000, MonthlyRequests: 5}.OperationsAllowance(), "100M operations/month"},
		{"thousand operations", Plan{OperationsPerMonth: 250_000}.OperationsAllowance(), "250K operations/month"},
		{"fractional millions", Plan{OperationsPerMonth: 1_500_000}.OperationsAllowance(), "1.5M operations/month"},
		{"billions", Plan{OperationsPerMonth: 1_000_000_000}.OperationsAllowance(), "1B operations/month"},
		{"inexact count", Plan{OperationsPerMonth: 1_234_567}.OperationsAllowance(), "1234567 operations/month"},
		{"requests", Plan{MonthlyRequests: 1_500}.OperationsAllowance(), "1500 requests/month"},
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
