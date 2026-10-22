package repllocal

import (
	"context"
	"errors"
	"fmt"
	"io"
	"math"
	"net/http"
	"strconv"
	"strings"
)

type Snapshot struct {
	CPUms, Allocated, FeedReads, MergedGroups float64
	Raw                                       string
}

// Snapshots are cumulative. Callers must reject falling counters and compare only within one PID.
func Scrape(ctx context.Context, endpoint string) (Snapshot, error) {
	var s Snapshot
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return s, err
	}
	// Request legacy text so exporter name normalization is deterministic.
	req.Header.Set("Accept", "text/plain; version=0.0.4")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return s, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	s.Raw = string(body)
	if err != nil {
		return s, err
	}
	if resp.StatusCode != http.StatusOK {
		return s, fmt.Errorf("metrics HTTP status %d", resp.StatusCode)
	}
	// OTel normalizes dots, appends the UCUM unit, then appends _total for counters.
	names := []string{"dotnet_process_cpu_time_seconds_total", "dotnet_gc_heap_total_allocated_bytes_total", "terrabase_storage_reads_total", "terrabase_replication_merged_groups_total"}
	values := []*float64{&s.CPUms, &s.Allocated, &s.FeedReads, &s.MergedGroups}
	var appeared, found [4]bool
	for line := range strings.SplitSeq(s.Raw, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		if strings.HasPrefix(line, "#") {
			fields := strings.Fields(line)
			if len(fields) >= 3 && (fields[1] == "TYPE" || fields[1] == "HELP") {
				for i, name := range names {
					if fields[2] == name {
						appeared[i] = true
					}
				}
			}
			continue
		}
		end := strings.IndexAny(line, "{ \t")
		if end < 0 {
			end = len(line)
		}
		for i, name := range names {
			if line[:end] != name {
				continue
			}
			appeared[i] = true
			path, value, err := metricSample(line[end:])
			if err != nil {
				return s, fmt.Errorf("%s: %w", name, err)
			}
			if i == 2 && path != "feed" {
				if path == "" {
					return s, fmt.Errorf("%s: missing path label", name)
				}
				found[i] = true // A known storage instrument with no feed series has made zero feed reads.
				continue
			}
			found[i] = true
			*values[i] += value
		}
	}
	for i, name := range names {
		if !found[i] && (i < 2 || appeared[i]) {
			return s, fmt.Errorf("missing metric sample %s", name)
		}
	}
	s.CPUms *= 1000
	return s, nil
}

func metricSample(rest string) (string, float64, error) {
	path := ""
	if strings.HasPrefix(rest, "{") {
		rest = rest[1:]
		for !strings.HasPrefix(strings.TrimSpace(rest), "}") {
			key, tail, ok := strings.Cut(rest, "=")
			if !ok {
				return "", 0, errors.New("invalid labels")
			}
			tail = strings.TrimSpace(tail)
			quoted, err := strconv.QuotedPrefix(tail)
			if err != nil {
				return "", 0, err
			}
			value, err := strconv.Unquote(quoted)
			if err != nil {
				return "", 0, err
			}
			if strings.TrimSpace(key) == "path" {
				path = value
			}
			rest = strings.TrimSpace(tail[len(quoted):])
			if strings.HasPrefix(rest, ",") {
				rest = rest[1:]
			} else if !strings.HasPrefix(rest, "}") {
				return "", 0, errors.New("invalid label separator")
			}
		}
		rest = strings.TrimSpace(rest)[1:]
	}
	fields := strings.Fields(rest)
	if len(fields) == 0 {
		return "", 0, errors.New("missing value")
	}
	v, err := strconv.ParseFloat(fields[0], 64)
	if err != nil || math.IsNaN(v) || math.IsInf(v, 0) || v < 0 {
		return "", 0, errors.New("invalid cumulative value")
	}
	return path, v, nil
}
