package render

import (
	"net/http/httptest"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestShellReturnURLIsTheCurrentSection(t *testing.T) {
	t.Parallel()
	for target, want := range map[string]string{
		"/":                                    "/",
		"/?tab=connect&range=24h":              "/",
		"/databases":                           "/databases",
		"/databases/db_1/data?collection=a":    "/databases",
		"/auth":                                "/auth/overview",
		"/auth/overview":                       "/auth/overview",
		"/auth/users?q=ada&page=2":             "/auth/users",
		"/usage?project=prj_1&tab=billing":     "/usage",
		"/organization?newOrganization=create": "/organization",
		"/profile?tab=security":                "/profile?tab=security",
		"//evil.example/path":                  "/",
	} {
		shell := shellFor(core.Account{}, core.Principal{}, "shell", httptest.NewRequest("GET", target, nil))
		if shell.ReturnURL != want {
			t.Errorf("ReturnURL on %s = %q, want %q", target, shell.ReturnURL, want)
		}
	}
}
