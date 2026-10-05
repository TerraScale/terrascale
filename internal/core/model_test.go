package core

import "testing"

func TestDefaultProjectID(t *testing.T) {
	tests := []struct {
		name     string
		projects map[string]Project
		want     string
	}{
		{name: "no projects", want: ""},
		{
			name: "marked default",
			projects: map[string]Project{
				"prj_a": {OrganizationID: "org_1"},
				"prj_b": {OrganizationID: "org_1", IsDefault: true},
			},
			want: "prj_b",
		},
		{
			name: "first by ID without a default",
			projects: map[string]Project{
				"prj_c": {OrganizationID: "org_1"},
				"prj_a": {OrganizationID: "org_1"},
			},
			want: "prj_a",
		},
		{
			name: "skips another organization's projects",
			projects: map[string]Project{
				"prj_a": {OrganizationID: "org_2", IsDefault: true},
				"prj_b": {OrganizationID: "org_1"},
			},
			want: "prj_b",
		},
		{
			name:     "only another organization's projects",
			projects: map[string]Project{"prj_a": {OrganizationID: "org_2"}},
			want:     "",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			org := Organization{ID: "org_1", Projects: tt.projects}
			if got := org.DefaultProjectID(); got != tt.want {
				t.Fatalf("DefaultProjectID() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestValidDatabaseName(t *testing.T) {
	for name, want := range map[string]bool{
		"orders": true, "a": true, "core-primary-2": true, "0-9": true,
		"": false, "-orders": false, "orders-": false, "Orders": false, "orders_v2": false,
		"core primary": false, "a/b": false, "Restored_UPPER!": false,
		"abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijk":  true,
		"abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl": false,
	} {
		if got := ValidDatabaseName(name); got != want {
			t.Errorf("ValidDatabaseName(%q) = %v, want %v", name, got, want)
		}
	}
}
