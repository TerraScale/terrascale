package main

import (
	"errors"
	"fmt"
	"net/netip"
	"os"
	"regexp"
	"slices"
	"strings"
)

// parityRequired are the serving regions every parity run must measure.
var parityRequired = []string{"gru", "sjc", "fra"}

// Node and region names follow TerraBase's peers-file rules (TerraBaseOptions).
var (
	parityNodeName   = regexp.MustCompile(`^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`)
	parityRegionName = regexp.MustCompile(`^[a-z]([a-z0-9-]{0,61}[a-z0-9])?$`)
)

// parityRegions reads the ts-orch peers file: one "<node> <region> <ipv4>" row
// per serving node. Blank and # rows are skipped; any other malformed row
// rejects the file rather than silently dropping a region. The result is the
// sorted, deduplicated region list, which must include gru, sjc and fra.
func parityRegions(path string) ([]string, error) {
	if path == "" {
		return nil, errors.New("parity needs TS_PEERS_FILE from ts-orch")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, errors.New("parity peers file could not be read")
	}
	var regions []string
	for number, line := range strings.Split(string(data), "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) != 3 || !parityNodeName.MatchString(fields[0]) || !parityRegionName.MatchString(fields[1]) {
			return nil, fmt.Errorf("parity peers file row %d is not <node> <region> <ipv4>", number+1)
		}
		if address, err := netip.ParseAddr(fields[2]); err != nil || !address.Is4() {
			return nil, fmt.Errorf("parity peers file row %d is not <node> <region> <ipv4>", number+1)
		}
		regions = append(regions, fields[1])
	}
	slices.Sort(regions)
	regions = slices.Compact(regions)
	for _, required := range parityRequired {
		if !slices.Contains(regions, required) {
			return nil, fmt.Errorf("parity required serving region %s is absent from the peers file", required)
		}
	}
	return regions, nil
}
