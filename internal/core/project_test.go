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
		Databases: map[string]Database{"5M9HQK3SHZJNB4SF7HKQKKEE46": {ID: "5M9HQK3SHZJNB4SF7HKQKKEE46"}, "71ZTRZXEQMCTB8QMFYBGTH36EF": {ID: "71ZTRZXEQMCTB8QMFYBGTH36EF"}},
		APIKeys: map[string]APIKey{
			"0R8KHHG2DSDN4T7NZ7R4698KW1": {ID: "0R8KHHG2DSDN4T7NZ7R4698KW1", DatabaseID: "5M9HQK3SHZJNB4SF7HKQKKEE46"},
			"0GHKN8622KZSA236B1XJV8G1JA": {ID: "0GHKN8622KZSA236B1XJV8G1JA", DatabaseID: "5M9HQK3SHZJNB4SF7HKQKKEE46", RevokedAt: earlier},
			"4TQK4AJ2R0GHVT42XR53DKZH9Y": {ID: "4TQK4AJ2R0GHVT42XR53DKZH9Y", DatabaseID: "71ZTRZXEQMCTB8QMFYBGTH36EF"},
			"6J3PEQTTERR7271PVFZZB4P4VQ": {ID: "6J3PEQTTERR7271PVFZZB4P4VQ"},
		},
		SharedResultConfigs: map[string]SharedResultConfig{
			"4AC0DRGVMPTDT49152ERCB29DG": {ID: "4AC0DRGVMPTDT49152ERCB29DG", DatabaseID: "5M9HQK3SHZJNB4SF7HKQKKEE46"},
			"6VDKZHRTFEFMEV2H0VKVE8Y262": {ID: "6VDKZHRTFEFMEV2H0VKVE8Y262", DatabaseID: "71ZTRZXEQMCTB8QMFYBGTH36EF"},
		},
		BackupPolicies: map[string]BackupPolicy{"5M9HQK3SHZJNB4SF7HKQKKEE46": {RetentionDays: 7}, "71ZTRZXEQMCTB8QMFYBGTH36EF": {RetentionDays: 30}},
	}

	project.RemoveDatabase("5M9HQK3SHZJNB4SF7HKQKKEE46", now)

	if got := slices.Sorted(maps.Keys(project.Databases)); !slices.Equal(got, []string{"71ZTRZXEQMCTB8QMFYBGTH36EF"}) {
		t.Errorf("databases = %v, want [71ZTRZXEQMCTB8QMFYBGTH36EF]", got)
	}
	if got := slices.Sorted(maps.Keys(project.SharedResultConfigs)); !slices.Equal(got, []string{"6VDKZHRTFEFMEV2H0VKVE8Y262"}) {
		t.Errorf("shared result configurations = %v, want [6VDKZHRTFEFMEV2H0VKVE8Y262]", got)
	}
	if got := slices.Sorted(maps.Keys(project.BackupPolicies)); !slices.Equal(got, []string{"71ZTRZXEQMCTB8QMFYBGTH36EF"}) {
		t.Errorf("backup policies = %v, want [71ZTRZXEQMCTB8QMFYBGTH36EF]", got)
	}
	for id, want := range map[string]time.Time{"0R8KHHG2DSDN4T7NZ7R4698KW1": now, "0GHKN8622KZSA236B1XJV8G1JA": earlier, "4TQK4AJ2R0GHVT42XR53DKZH9Y": {}, "6J3PEQTTERR7271PVFZZB4P4VQ": {}} {
		if got := project.APIKeys[id].RevokedAt; !got.Equal(want) {
			t.Errorf("%s revoked at %v, want %v", id, got, want)
		}
	}
}
