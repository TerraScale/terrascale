package onboarding

// The demo values preserve the original onboarding fixture. Live onboarding
// has no provider latency measurements yet; never present fixtures as live RTT.
func projectRegionPings(demo bool) map[string]string {
	if demo {
		return map[string]string{
			"GRU": "Ping: 118 ms (demo)",
			"SFO": "Ping: 28 ms (demo)",
		}
	}
	return map[string]string{
		"GRU": "Ping unavailable",
		"SFO": "Ping unavailable",
	}
}
