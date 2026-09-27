package auth

import "testing"

func TestSafeRedirectTargetDropsStatusMessages(t *testing.T) {
	for raw, want := range map[string]string{
		"/settings?error=Call+us&tab=identity": "/settings?tab=identity",
		"/admin/plans?notice=Call+us":          "/admin/plans",
		"/settings?toast=Call+us":              "/settings",
		"/api-keys?kind=m2m":                   "/api-keys?kind=m2m",
		"https://evil.example/?error=Call+us":  "/",
	} {
		if got := safeRedirectTarget(raw); got != want {
			t.Errorf("safeRedirectTarget(%q) = %q, want %q", raw, got, want)
		}
	}
}
