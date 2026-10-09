package backups

import (
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestRestoredNameFitsLimitWithSuffix(t *testing.T) {
	long := strings.Repeat("a", 60)
	project := core.Project{Databases: map[string]core.Database{
		"19T9WF6BRDZ1HTM3X1C9G1V4YJ": {Name: "orders-restore"},
		"1HVDPNK0T6S19MHKKAKCJEYFZS": {Name: long[:55] + "-restore"},
	}}
	tests := []struct{ source, want string }{
		{source: "users", want: "users-restore"},
		{source: "orders", want: "orders-restore-2"},
		{source: long, want: long[:53] + "-restore-2"},
	}
	for _, tt := range tests {
		if got := restoredName(project, tt.source); got != tt.want || len(got) > 63 {
			t.Errorf("restoredName(%q) = %q, want %q", tt.source, got, tt.want)
		}
	}
}
