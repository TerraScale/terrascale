package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"os"
	"time"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/usage"
)

// reportInternalBilling is an explicit operator job. The access token is a
// signed-in ts-auth member's token, never a TerraBase management credential.
func reportInternalBilling(args []string) error {
	flags := flag.NewFlagSet("report-internal-billing", flag.ContinueOnError)
	org := flags.String("org", "", "exact internal organization ID")
	email := flags.String("admin-email", "", "existing platform administrator")
	if err := flags.Parse(args); err != nil {
		return err
	}
	token := os.Getenv("TS_INTERNAL_BILLING_ACCESS_TOKEN")
	if *org == "" || *email == "" || flags.NArg() != 0 || token == "" {
		return errors.New("--org, --admin-email and TS_INTERNAL_BILLING_ACCESS_TOKEN are required")
	}
	cfg, db, err := openDatabase()
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	store := terrabase.New(db)
	admin, err := store.FindByEmail(ctx, *email)
	if err != nil {
		return err
	}
	service := usage.NewService(core.Deps{Store: store, Usage: terrabase.Meter{BaseURL: cfg.TerraBaseURL, Client: cfg.TerraBaseHTTPClient}})
	bill, err := service.CheckInternalBilling(ctx, admin.Principal(), *org, token)
	if bill != nil {
		if encodeErr := json.NewEncoder(os.Stdout).Encode(bill); encodeErr != nil {
			return encodeErr
		}
	}
	return err
}
