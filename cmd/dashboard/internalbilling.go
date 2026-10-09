package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"strings"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/features/subscriptions"
)

func configureInternalBilling(args []string) error {
	config, email, err := parseInternalBillingConfig(args)
	if err != nil {
		return err
	}
	_, db, err := openDatabase()
	if err != nil {
		return err
	}
	ctx := context.Background()
	store := terrabase.New(db)
	admin, err := store.FindByEmail(ctx, email)
	if err != nil {
		return fmt.Errorf("find existing platform admin: %w", err)
	}
	if err := subscriptions.ConfigureInternalBilling(ctx, store, admin.Principal(), config); err != nil {
		return err
	}
	fmt.Printf("configured report-only internal billing for organization %s using base plan %s\n", config.OrganizationID, config.BasePlanKey)
	return nil
}

func parseInternalBillingConfig(args []string) (subscriptions.InternalBillingConfig, string, error) {
	var config subscriptions.InternalBillingConfig
	flags := flag.NewFlagSet("configure-internal-billing", flag.ContinueOnError)
	flags.StringVar(&config.OrganizationID, "org", "", "exact existing terrascale organization ID")
	email := flags.String("admin-email", "", "email of an existing platform admin")
	flags.StringVar(&config.BasePlanKey, "base-plan", "", "existing paid base plan key, for example business")
	flags.Int64Var(&config.SpendCapCents, "spend-cap-cents", 0, "positive report-only organization spend cap in cents")
	flags.Int64Var(&config.WriteCentsPerMillion, "write-cents-per-million", 0, "explicit internal write estimate rate; zero is allowed")
	flags.Int64Var(&config.StorageCentsPerGiBMonth, "storage-cents-per-gib-month", 0, "explicit internal storage estimate rate; zero is allowed")
	if err := flags.Parse(args); err != nil {
		return config, "", err
	}
	provided := make(map[string]bool)
	flags.Visit(func(f *flag.Flag) { provided[f.Name] = true })
	for _, name := range []string{"org", "admin-email", "base-plan", "spend-cap-cents", "write-cents-per-million", "storage-cents-per-gib-month"} {
		if !provided[name] {
			return config, "", fmt.Errorf("--%s is required", name)
		}
	}
	if flags.NArg() != 0 {
		return config, "", errors.New("unexpected positional arguments")
	}
	if strings.TrimSpace(config.OrganizationID) == "" || strings.TrimSpace(*email) == "" || strings.TrimSpace(config.BasePlanKey) == "" {
		return config, "", errors.New("--org, --admin-email and --base-plan must not be empty")
	}
	if strings.TrimSpace(config.OrganizationID) != config.OrganizationID || strings.TrimSpace(config.BasePlanKey) != config.BasePlanKey {
		return config, "", errors.New("--org and --base-plan must be exact IDs and keys without surrounding whitespace")
	}
	if config.SpendCapCents <= 0 || config.WriteCentsPerMillion < 0 || config.StorageCentsPerGiBMonth < 0 {
		return config, "", errors.New("--spend-cap-cents must be positive and estimate rates non-negative")
	}
	return config, strings.TrimSpace(*email), nil
}
