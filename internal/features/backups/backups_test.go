package backups

import "testing"

func TestValidCron(t *testing.T) {
	for schedule, want := range map[string]bool{
		"":                true,
		"0 2 * * *":       true,
		"*/15 * * * 1-5":  true,
		"0 0,12 1 */2 *":  true,
		"every day":       false,
		"0 2 * *":         false,
		"0 2 * * * *":     false,
		"0 2 * * MON":     false,
		"0 2 * * *; rm -": false,
		"- , / - ,":       false,
	} {
		if got := validCron(schedule); got != want {
			t.Errorf("validCron(%q) = %v, want %v", schedule, got, want)
		}
	}
}
