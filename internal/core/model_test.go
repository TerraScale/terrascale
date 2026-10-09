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
				"7PRR0P2V1D3B8TWN2SBH8H56YQ": {OrganizationID: "1MZ7GT1142NZMAQXJE73TYTFZW"},
				"0RNQMHXN3S9APPXH49NK7HKTC6": {OrganizationID: "1MZ7GT1142NZMAQXJE73TYTFZW", IsDefault: true},
			},
			want: "0RNQMHXN3S9APPXH49NK7HKTC6",
		},
		{
			name: "first by ID without a default",
			projects: map[string]Project{
				"4RHQAH9DY169DF0EFHQPCR12JV": {OrganizationID: "1MZ7GT1142NZMAQXJE73TYTFZW"},
				"7PRR0P2V1D3B8TWN2SBH8H56YQ": {OrganizationID: "1MZ7GT1142NZMAQXJE73TYTFZW"},
			},
			want: "4RHQAH9DY169DF0EFHQPCR12JV",
		},
		{
			name: "skips another organization's projects",
			projects: map[string]Project{
				"7PRR0P2V1D3B8TWN2SBH8H56YQ": {OrganizationID: "74HHC6CSDPQX1TQ8CRT3750K68", IsDefault: true},
				"0RNQMHXN3S9APPXH49NK7HKTC6": {OrganizationID: "1MZ7GT1142NZMAQXJE73TYTFZW"},
			},
			want: "0RNQMHXN3S9APPXH49NK7HKTC6",
		},
		{
			name:     "only another organization's projects",
			projects: map[string]Project{"7PRR0P2V1D3B8TWN2SBH8H56YQ": {OrganizationID: "74HHC6CSDPQX1TQ8CRT3750K68"}},
			want:     "",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			org := Organization{ID: "1MZ7GT1142NZMAQXJE73TYTFZW", Projects: tt.projects}
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
