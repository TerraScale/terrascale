package repllocal

import (
	"context"
	"encoding/json/v2"
	"fmt"
	"math"
	"slices"
)

// CommitTiming retains exact correlation evidence, never worker discovery files or credentials.
type CommitTiming struct {
	Frequency     int64         `json:"frequency"`
	BootID        string        `json:"boot_id"`
	TimeNamespace string        `json:"time_namespace"`
	Incarnation   string        `json:"incarnation"`
	Origin        string        `json:"origin"`
	Dropped       int64         `json:"dropped"`
	Commits       []CommitPoint `json:"commits"`
	Applies       []ApplyPoint  `json:"applies"`
}

type CommitPoint struct {
	Marker     int64 `json:"marker"`
	Committed  int64 `json:"committed"`
	Identified int64 `json:"identified"`
}

type ApplyPoint struct {
	Origin          string           `json:"origin"`
	Marker          int64            `json:"marker"`
	Entered         int64            `json:"entered"`
	Completed       int64            `json:"completed"`
	NativeCompleted int64            `json:"native_completed"`
	Documents       []TimingDocument `json:"documents"`
}

type TimingDocument struct {
	Collection string `json:"collection"`
	ID         string `json:"id"`
}

func (c *Cluster) CommitTimings(ctx context.Context) (map[string]CommitTiming, error) {
	result := map[string]CommitTiming{}
	for region := range c.Nodes {
		b, err := c.sampleWorker(ctx, region)
		if err != nil {
			return nil, err
		}
		var sample struct {
			Timing CommitTiming `json:"commit_timing"`
		}
		if err := json.Unmarshal(b, &sample); err != nil {
			return nil, err
		}
		result[region] = sample.Timing
	}
	return result, nil
}

// Same-host CLOCK_MONOTONIC pairs at each transaction's existing post-native-COMMIT hook.
func CommitLag(source, receiver CommitTiming, collection string, expected int) (map[string]float64, error) {
	if expected < 1 || source.Dropped != 0 || receiver.Dropped != 0 || source.Frequency != 1_000_000_000 || receiver.Frequency != source.Frequency || source.BootID == "" ||
		source.BootID != receiver.BootID || source.TimeNamespace == "" || source.TimeNamespace != receiver.TimeNamespace ||
		source.Incarnation == "" || receiver.Incarnation == "" || source.Origin == "" || receiver.Origin == "" || source.Origin == receiver.Origin {
		return nil, fmt.Errorf("commit timing requires distinct stable processes on one Linux monotonic clock")
	}
	commits := map[int64]CommitPoint{}
	for _, p := range source.Commits {
		if _, exists := commits[p.Marker]; exists || p.Marker <= 0 || p.Committed <= 0 || p.Identified < p.Committed {
			return nil, fmt.Errorf("invalid or duplicate source commit marker %d", p.Marker)
		}
		commits[p.Marker] = p
	}
	seen := map[string]bool{}
	groups := map[int64]bool{}
	values := map[string][]float64{}
	for _, a := range receiver.Applies {
		if a.Origin != source.Origin {
			continue
		}
		var ids []string
		for _, d := range a.Documents {
			if d.Collection == collection {
				ids = append(ids, d.ID)
			}
		}
		if len(ids) == 0 {
			continue
		}
		p, ok := commits[a.Marker]
		if !ok || groups[a.Marker] || a.Entered < p.Committed || a.NativeCompleted < a.Entered || a.Completed < a.NativeCompleted {
			return nil, fmt.Errorf("missing, duplicate or noncausal apply for marker %d", a.Marker)
		}
		groups[a.Marker] = true
		lag := float64(a.NativeCompleted-p.Committed) / 1e6
		values["commit_to_apply.group"] = append(values["commit_to_apply.group"], lag)
		values["marker_identification"] = append(values["marker_identification"], float64(p.Identified-p.Committed)/1e6)
		for _, id := range ids {
			if id == "" || seen[id] {
				return nil, fmt.Errorf("empty or duplicate timed document %s", id)
			}
			seen[id] = true
			values["commit_to_apply.document"] = append(values["commit_to_apply.document"], lag)
			values["commit_to_sink_entry.document"] = append(values["commit_to_sink_entry.document"], float64(a.Entered-p.Committed)/1e6)
			values["sink_apply.document"] = append(values["sink_apply.document"], float64(a.NativeCompleted-a.Entered)/1e6)
			values["apply_cleanup.document"] = append(values["apply_cleanup.document"], float64(a.Completed-a.NativeCompleted)/1e6)
		}
	}
	if len(seen) != expected {
		return nil, fmt.Errorf("timed %d documents, expected %d", len(seen), expected)
	}
	metrics := map[string]float64{}
	for name, xs := range values {
		slices.Sort(xs)
		metrics[name+".count"] = float64(len(xs))
		metrics[name+".p50_ms"] = xs[int(math.Ceil(float64(len(xs))*.5))-1]
		metrics[name+".p99_ms"] = xs[int(math.Ceil(float64(len(xs))*.99))-1]
		metrics[name+".max_ms"] = xs[len(xs)-1]
	}
	return metrics, nil
}
