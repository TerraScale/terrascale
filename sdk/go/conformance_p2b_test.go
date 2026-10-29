// Conformance consumes the pinned producer contracts and wire vectors.
// Transport fixtures establish SDK behavior, not live API qualification.
package sdk_test

import (
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestP2BConformanceCoreSurfaceHasNoSyntheticTransport(t *testing.T) {
	t.Parallel()
	if sdk.DefaultAPIBaseURL != "https://api.terrascale.tech" {
		t.Fatal("SDK API endpoint must be the fixed public origin")
	}
	if _, err := sdk.NewClient(sdk.Config{APIBaseURL: "https://tbep.terrascale.tech"}); err == nil {
		t.Fatal("TBEP must not be accepted as the public SDK endpoint")
	}
	if _, err := sdk.ParseRegionCode("gru"); err != nil {
		t.Fatalf("region parsing is not case-insensitive: %v", err)
	}
	if _, _, err := sdk.Select("", "id").Build(); err == nil {
		t.Fatal("empty collection must fail closed")
	}
	if _, _, err := sdk.Select("users", "id").Limit(-1).Build(); err == nil {
		t.Fatal("negative SQL limit must fail closed")
	}
}
