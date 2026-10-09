package payments

import "terrascale.tech/sdk/v1"

// NewStripeForTest wires a fake provider and state store into the live
// billing routes.
func NewStripeForTest(config StripeConfig, provider BillingProvider, store StateStore) *Stripe {
	return newStripe(config, provider, store)
}

// NewMemoryStateStore returns the in-memory StateStore used by tests.
func NewMemoryStateStore() StateStore { return newMemoryStateStore() }

func NewTerraBaseStateStoreForTest(db *v1.Database) StateStore { return newTerraBaseStateStore(db) }
