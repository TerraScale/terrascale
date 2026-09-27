package backups

import (
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestRestoredNameFitsLimitWithSuffix(t *testing.T) {
	long := strings.Repeat("a", 60)
	project := core.Project{Databases: map[string]core.Database{
		"db_1": {Name: "orders-restore"},
		"db_2": {Name: long[:55] + "-restore"},
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
