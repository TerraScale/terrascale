package main

import (
	"fmt"
	"io"
)

// Billing and Stripe consent stay in the dashboard. This link requires no token.
func showBilling(out io.Writer) error {
	const address = "https://dash.terrascale.tech/billing"
	if jsonMode(out) {
		return emitJSON(out, struct {
			URL string `json:"url"`
		}{address})
	}
	_, err := fmt.Fprintln(out, address)
	return err
}
