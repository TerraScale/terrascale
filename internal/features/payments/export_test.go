package payments

// NewStripeForTest wires a fake provider into the live billing routes.
func NewStripeForTest(config StripeConfig, provider BillingProvider) *Stripe {
	return newStripe(config, provider)
}
