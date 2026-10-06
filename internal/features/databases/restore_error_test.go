package databases

import (
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestRestoreErrorShowsOnThePageWithoutItsDialog(t *testing.T) {
	refused := func(backupID string, canRestore bool) detailData {
		return detailData{
			Error:  formError(ErrBackupMissing),
			Refill: backupRefill{RestoreBackupID: backupID},
			Info:   detailInfo{Backups: []core.Backup{{ID: "bkp_1"}}, CanRestoreBackups: canRestore},
		}
	}
	for name, tc := range map[string]struct {
		data detailData
		want bool
	}{
		"listed backup":   {refused("bkp_1", true), true},
		"missing backup":  {refused("bkp_gone", true), false},
		"no restore role": {refused("bkp_1", false), false},
		"policy error":    {refused("", true), false},
	} {
		if got := restoreErrorInDialog(tc.data); got != tc.want {
			t.Errorf("%s: restoreErrorInDialog = %v, want %v", name, got, tc.want)
		}
	}
}
