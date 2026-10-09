package subscriptions

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

// InternalBillingConfig names an existing organization and paid base plan.
// Rates are explicit internal estimates; zero is valid and reads are free.
type InternalBillingConfig struct {
	OrganizationID          string
	BasePlanKey             string
	SpendCapCents           int64
	WriteCentsPerMillion    int64
	StorageCentsPerGiBMonth int64
}

// ConfigureInternalBilling assigns TerraScale its own hidden plan without
// provisioning identities, projects, keys or Stripe resources. Repeating the
// configuration commits the plan and cap together; rejected changes preserve
// the active policy. The store enforces one internal organization atomically.
func ConfigureInternalBilling(ctx context.Context, store core.Store, principal core.Principal, config InternalBillingConfig) error {
	if store == nil || principal.AccountID == "" || principal.UserID == "" {
		return core.ErrUnauthorized
	}
	if err := store.AuthorizePlatformAdmin(ctx, principal); err != nil {
		return err
	}
	if config.OrganizationID == "" || strings.TrimSpace(config.OrganizationID) != config.OrganizationID || config.BasePlanKey == "" {
		return core.Invalid("an exact organization ID and paid base plan key are required")
	}
	if config.SpendCapCents <= 0 || config.WriteCentsPerMillion < 0 || config.StorageCentsPerGiBMonth < 0 {
		return core.Invalid("the spend cap must be positive and estimate rates non-negative")
	}
	accounts, err := store.PlatformAccounts(ctx, principal)
	if err != nil {
		return err
	}
	targetFound := false
	for _, account := range accounts {
		for id, org := range account.Organizations {
			if id == config.OrganizationID {
				if err := internalBillingTarget(org); err != nil {
					return err
				}
				targetFound = true
			} else if org.Billing.Internal {
				return fmt.Errorf("%w: internal billing already belongs to another organization", core.ErrConflict)
			}
		}
	}
	if !targetFound {
		return fmt.Errorf("%w: organization %q", core.ErrNotFound, config.OrganizationID)
	}
	plan, err := store.Plan(ctx, config.BasePlanKey)
	if err != nil {
		return err
	}
	if plan.Key != config.BasePlanKey || plan.PriceCents <= 0 || plan.Key == "internal" {
		return core.Invalid("select an existing paid base plan key")
	}
	internal, err := store.Plan(ctx, "internal")
	switch {
	case err == nil:
		plan.ID = internal.ID
	case errors.Is(err, core.ErrNotFound):
		plan.ID = core.NewID()
	default:
		return err
	}
	plan.Key, plan.Name, plan.Visible = "internal", "TerraScale Internal", false
	plan.WriteCentsPerMillion, plan.StorageCentsPerGiBMonth = config.WriteCentsPerMillion, config.StorageCentsPerGiBMonth
	return store.UpsertPlanAndUpdateOrganizationAs(ctx, principal, config.OrganizationID, plan, func(org *core.Organization) error {
		if err := internalBillingTarget(*org); err != nil {
			return err
		}
		org.PlanID, org.BillingStatus = plan.ID, "active"
		org.Billing.Internal = true
		org.Billing.Status, org.Billing.PlanKey, org.Billing.Interval = "active", plan.Key, "monthly"
		org.Billing.PaidExcessEnabled, org.Billing.SpendCapCents = true, config.SpendCapCents
		return nil
	})
}

func internalBillingTarget(org core.Organization) error {
	if org.Name != "terrascale" {
		return fmt.Errorf("%w: internal billing requires the organization named exactly terrascale", core.ErrForbidden)
	}
	if org.Status != "active" || org.Archived || org.DeletionRequested {
		return fmt.Errorf("%w: the internal organization must be active", core.ErrConflict)
	}
	for _, service := range []string{"ts-dashboard", "ts-auth", "ts-router", "ts-status", "ts-orchestrator", "ku", "ts-reviewer"} {
		found := false
		for _, project := range org.Projects {
			if project.Name == service && project.Status == "active" {
				found = true
				break
			}
		}
		if !found {
			return fmt.Errorf("%w: internal service project %s is missing or inactive", core.ErrConflict, service)
		}
	}
	billing := org.Billing
	if billing.CustomerID != "" || billing.SubscriptionID != "" || billing.CheckoutID != "" || billing.ScheduleID != "" ||
		billing.PendingPlanKey != "" || billing.ScheduledPlanKey != "" || len(org.CheckoutSessions) != 0 {
		return fmt.Errorf("%w: internal billing cannot replace Stripe billing or checkout state", core.ErrConflict)
	}
	return nil
}
