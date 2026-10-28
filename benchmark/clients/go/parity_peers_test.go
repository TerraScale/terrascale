package main

import (
	"os"
	"path/filepath"
	"slices"
	"testing"
)

func TestParityRegionsFromPeersFile(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name, peers string
		want        []string
	}{
		{"sorted-deduplicated", "# ts-orch\nts-sjc-1 sjc 100.64.0.2\n\nts-gru-1 gru 100.64.0.1\nts-gru-2 gru 100.64.0.4\nts-fra-1 fra 100.64.0.3\n", []string{"fra", "gru", "sjc"}},
		{"extra-region-kept", "a gru 10.0.0.1\nb sjc 10.0.0.2\nc fra 10.0.0.3\nd iad 10.0.0.4\n", []string{"fra", "gru", "iad", "sjc"}},
		{"missing-required", "a gru 10.0.0.1\nb sjc 10.0.0.2\n", nil},
		{"ipv6-rejected", "a gru 10.0.0.1\nb sjc 10.0.0.2\nc fra fd00::1\n", nil},
		{"extra-field-rejected", "a gru 10.0.0.1\nb sjc 10.0.0.2\nc fra 10.0.0.3 extra\n", nil},
		{"bad-region-rejected", "a gru 10.0.0.1\nb sjc 10.0.0.2\nc fra 10.0.0.3\nd FRA 10.0.0.4\n", nil},
		{"empty", "", nil},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			path := filepath.Join(t.TempDir(), "peers")
			if err := os.WriteFile(path, []byte(test.peers), 0o600); err != nil {
				t.Fatal(err)
			}
			regions, err := parityRegions(path)
			if test.want == nil {
				if err == nil {
					t.Fatalf("accepted %v", regions)
				}
				return
			}
			if err != nil || !slices.Equal(regions, test.want) {
				t.Fatalf("regions %v, %v; want %v", regions, err, test.want)
			}
		})
	}
	if _, err := parityRegions(""); err == nil {
		t.Fatal("unset TS_PEERS_FILE accepted")
	}
	if _, err := parityRegions(filepath.Join(t.TempDir(), "absent")); err == nil {
		t.Fatal("absent peers file accepted")
	}
}
