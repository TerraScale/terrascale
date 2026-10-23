package repllocal

import (
	"fmt"
	"testing"
)

func timingFixture() (CommitTiming, CommitTiming) {
	s := CommitTiming{Frequency: 1e9, BootID: "boot", TimeNamespace: "time:[1]", Incarnation: "source-copy", Origin: "source",
		Commits: []CommitPoint{{Marker: 1, Committed: 1e9, Identified: 1e9 + 1e5}, {Marker: 2, Committed: 2e9, Identified: 2e9 + 1e5}}}
	r := CommitTiming{Frequency: 1e9, BootID: "boot", TimeNamespace: "time:[1]", Incarnation: "remote-copy", Origin: "remote",
		Applies: []ApplyPoint{{Origin: "source", Marker: 1, Entered: 1e9 + 1e6, NativeCompleted: 1e9 + 10e6, Completed: 1e9 + 60e6},
			{Origin: "source", Marker: 2, Entered: 2e9 + 1e6, NativeCompleted: 2e9 + 100e6, Completed: 2e9 + 200e6, Documents: []TimingDocument{{Collection: "load", ID: "slow"}}}}}
	for i := range 100 {
		r.Applies[0].Documents = append(r.Applies[0].Documents, TimingDocument{Collection: "load", ID: fmt.Sprint(i)})
	}
	return s, r
}

func TestCommitLagPairsMarkersAndWeightsEveryDocument(t *testing.T) {
	s, r := timingFixture()
	m, err := CommitLag(s, r, "load", 101)
	if err != nil {
		t.Fatal(err)
	}
	for key, want := range map[string]float64{"commit_to_apply.document.count": 101, "commit_to_apply.group.count": 2,
		"commit_to_apply.document.p99_ms": 10, "commit_to_apply.group.p99_ms": 100,
		"commit_to_sink_entry.document.p99_ms": 1, "sink_apply.document.p99_ms": 9, "marker_identification.p99_ms": .1, "apply_cleanup.document.p99_ms": 50} {
		if m[key] != want {
			t.Fatalf("%s = %v, want %v", key, m[key], want)
		}
	}
}

func TestCommitLagRejectsIncompleteOrInvalidEvidence(t *testing.T) {
	for _, name := range []string{"clock", "namespace", "origin", "missing-marker", "duplicate-marker", "duplicate-apply", "duplicate-doc", "negative", "native-missing", "native-after-return", "dropped", "incomplete"} {
		t.Run(name, func(t *testing.T) {
			s, r := timingFixture()
			switch name {
			case "clock":
				r.BootID = "other"
			case "namespace":
				r.TimeNamespace = "other"
			case "origin":
				r.Applies[0].Origin = "other"
			case "missing-marker":
				s.Commits = s.Commits[1:]
			case "duplicate-marker":
				s.Commits = append(s.Commits, s.Commits[0])
			case "duplicate-apply":
				r.Applies = append(r.Applies, r.Applies[0])
			case "duplicate-doc":
				r.Applies[0].Documents[0].ID = "slow"
			case "negative":
				r.Applies[0].Entered = s.Commits[0].Committed - 1
			case "native-missing":
				r.Applies[0].NativeCompleted = 0
			case "native-after-return":
				r.Applies[0].NativeCompleted = r.Applies[0].Completed + 1
			case "dropped":
				r.Dropped = 1
			case "incomplete":
				r.Applies[0].Documents = r.Applies[0].Documents[1:]
			}
			if _, err := CommitLag(s, r, "load", 101); err == nil {
				t.Fatal("invalid evidence accepted")
			}
		})
	}
}

func TestCommitLagAllowsMarkerIdentificationToOverlapReceiver(t *testing.T) {
	s, r := timingFixture()
	s.Commits[0].Identified = r.Applies[0].Entered + 1e6
	m, err := CommitLag(s, r, "load", 101)
	if err != nil || m["commit_to_apply.document.p99_ms"] != 10 {
		t.Fatalf("source marker lookup must not move the COMMIT boundary: metrics=%v error=%v", m, err)
	}
}
