package onboarding

import (
	"strings"
	"testing"
)

func TestLiveProjectTooltipsDoNotPresentDemoLatency(t *testing.T) {
	for code, ping := range projectRegionPings(false) {
		if strings.Contains(ping, "ms") || ping != "Ping unavailable" {
			t.Errorf("live %s tooltip = %q without provider measurements", code, ping)
		}
	}
}
