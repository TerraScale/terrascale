package core

import (
	"fmt"

	sdk "terrascale.tech/sdk"
)

// ParsePlacementRegion limits dashboard placement to the supported hubs.
func ParsePlacementRegion(value string) (sdk.RegionCode, error) {
	region, err := sdk.ParseRegionCode(value)
	if err != nil || region != sdk.RegionGRU && region != sdk.RegionSFO {
		return "", Invalid(fmt.Sprintf("unknown region %q; use GRU or SFO", value))
	}
	return region, nil
}
