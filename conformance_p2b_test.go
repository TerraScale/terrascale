// P-2b conformance is contract evidence, not a mock-server exercise.
//
// This gate consumes the pinned TerraBase OpenAPI document and the producer's
// hand-audited MessagePack corpus. HTTP behavior belongs in the producer
// conformance suite and in the integration journey against a real member; a
// synthetic server here could accept a protocol the member never serves.
package sdk_test

import (
	"context"
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestP2BConformanceCoreSurfaceHasNoSyntheticTransport(t *testing.T) {
	t.Parallel()
	if sdk.DefaultAPIBaseURL == "" || sdk.DefaultSyncBaseURL == "" {
		t.Fatal("SDK contract endpoints must be explicit")
	}
	if _, err := sdk.NewClient(sdk.Config{APIBaseURL: "https://tbep.terrascale.tech"}); err == nil {
		t.Fatal("TBEP must not be accepted as the public SDK endpoint")
	}
	if _, err := sdk.NewClient(sdk.Config{APIBaseURL: "https://api.terrascale.tech", Affinity: sdk.AffinityStrict}); err == nil {
		t.Fatal("strict affinity without a region must fail closed")
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
	if _, err := sdk.NewSchemaBuilder().Apply(context.Background(), nil, "db"); err == nil {
		t.Fatal("schema application without a client must fail closed")
	}
}
