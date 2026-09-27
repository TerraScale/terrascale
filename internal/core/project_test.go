package core

import (
	"maps"
	"slices"
	"testing"
	"time"
)

func TestRemoveDatabaseTakesItsScopedRecordsWithIt(t *testing.T) {
	now := time.Date(2026, time.September, 26, 12, 0, 0, 0, time.UTC)
	earlier := now.Add(-time.Hour)
	project := Project{
		Databases: map[string]Database{"db_gone": {ID: "db_gone"}, "db_kept": {ID: "db_kept"}},
		APIKeys: map[string]APIKey{
			"key_gone":    {ID: "key_gone", DatabaseID: "db_gone"},
			"key_revoked": {ID: "key_revoked", DatabaseID: "db_gone", RevokedAt: earlier},
			"key_kept":    {ID: "key_kept", DatabaseID: "db_kept"},
			"key_project": {ID: "key_project"},
		},
		SharedResultConfigs: map[string]SharedResultConfig{
			"cfg_gone": {ID: "cfg_gone", DatabaseID: "db_gone"},
			"cfg_kept": {ID: "cfg_kept", DatabaseID: "db_kept"},
		},
		BackupPolicies: map[string]BackupPolicy{"db_gone": {RetentionDays: 7}, "db_kept": {RetentionDays: 30}},
	}

	project.RemoveDatabase("db_gone", now)

	if got := slices.Sorted(maps.Keys(project.Databases)); !slices.Equal(got, []string{"db_kept"}) {
		t.Errorf("databases = %v, want [db_kept]", got)
	}
	if got := slices.Sorted(maps.Keys(project.SharedResultConfigs)); !slices.Equal(got, []string{"cfg_kept"}) {
		t.Errorf("shared result configurations = %v, want [cfg_kept]", got)
	}
	if got := slices.Sorted(maps.Keys(project.BackupPolicies)); !slices.Equal(got, []string{"db_kept"}) {
		t.Errorf("backup policies = %v, want [db_kept]", got)
	}
	for id, want := range map[string]time.Time{"key_gone": now, "key_revoked": earlier, "key_kept": {}, "key_project": {}} {
		if got := project.APIKeys[id].RevokedAt; !got.Equal(want) {
			t.Errorf("%s revoked at %v, want %v", id, got, want)
		}
	}
}
