package buildinfo

import (
	"runtime/debug"
	"testing"
)

func TestResolvePrefersStampedVersionThenVCSRevision(t *testing.T) {
	vcs := func(settings ...debug.BuildSetting) *debug.BuildInfo {
		return &debug.BuildInfo{Settings: settings}
	}
	revision := debug.BuildSetting{Key: "vcs.revision", Value: "4f2c9e1"}
	for _, tc := range []struct {
		name    string
		stamped string
		info    *debug.BuildInfo
		want    string
	}{
		{"stamped wins", "abc123", vcs(revision), "abc123"},
		{"vcs revision", "", vcs(revision), "4f2c9e1"},
		{"modified tree", "", vcs(revision, debug.BuildSetting{Key: "vcs.modified", Value: "true"}), "4f2c9e1-dirty"},
		{"clean tree", "", vcs(revision, debug.BuildSetting{Key: "vcs.modified", Value: "false"}), "4f2c9e1"},
		{"no vcs settings", "", vcs(), "dev"},
		{"no build info", "", nil, "dev"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if got := resolve(tc.stamped, tc.info); got != tc.want {
				t.Fatalf("resolve() = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestVersionIsNeverEmpty(t *testing.T) {
	if Version() == "" {
		t.Fatal("Version() is empty")
	}
}
