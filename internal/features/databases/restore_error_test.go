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
			Info:   detailInfo{Backups: []core.Backup{{ID: "3B5V4E9Q6J83SJW0R5JPYK146C"}}, CanRestoreBackups: canRestore},
		}
	}
	for name, tc := range map[string]struct {
		data detailData
		want bool
	}{
		"listed backup":   {refused("3B5V4E9Q6J83SJW0R5JPYK146C", true), true},
		"missing backup":  {refused("6EP5Y8DKRWF00619MAZE7BV6QD", true), false},
		"no restore role": {refused("3B5V4E9Q6J83SJW0R5JPYK146C", false), false},
		"policy error":    {refused("", true), false},
	} {
		if got := restoreErrorInDialog(tc.data); got != tc.want {
			t.Errorf("%s: restoreErrorInDialog = %v, want %v", name, got, tc.want)
		}
	}
}
