package main

import (
	"context"
	"errors"
	"flag"
	"fmt"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
)

// grantPlatformAdmin grants, or with --revoke revokes, platform
// administration for one account in the configured dashboard database. It is
// idempotent, so it is safe to rerun.
func grantPlatformAdmin(args []string) error {
	flags := flag.NewFlagSet("grant-platform-admin", flag.ContinueOnError)
	email := flags.String("email", "", "email of the account to change")
	revoke := flags.Bool("revoke", false, "revoke platform administration instead of granting it")
	if err := flags.Parse(args); err != nil {
		return err
	}
	if *email == "" {
		return errors.New("--email is required")
	}
	_, db, err := openDatabase()
	if err != nil {
		return err
	}
	ctx := context.Background()
	store := terrabase.New(db)
	account, err := store.FindByEmail(ctx, *email)
	if err != nil {
		return err
	}
	changed, err := store.SetPlatformAdmin(ctx, account.ID, !*revoke)
	if err != nil {
		return err
	}
	verb := "granted"
	if *revoke {
		verb = "revoked"
	}
	if !changed {
		verb = "already " + verb
	}
	fmt.Printf("platform admin %s for %s\n", verb, *email)
	return nil
}
