package onboarding

import (
	"cmp"
	"context"
	"errors"
	"slices"
	"strings"
	"unicode"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
)

// Service owns account setup after registration: the account, payment, and
// first-project steps. HTTP handlers and MCP tools both call it.
type Service struct {
	deps    core.Deps
	billing payments.Service
}

type status struct {
	Completed bool       `json:"completed"`
	Step      string     `json:"step" jsonschema:"Current step: account-information, payment-method, or project. Empty once setup is complete."`
	Steps     []stepView `json:"steps"`
	Plans     []planView `json:"plans" jsonschema:"Paid plans the payment step offers."`
}

type planView struct {
	Key              string `json:"key"`
	Name             string `json:"name"`
	PriceCents       int64  `json:"priceCents" jsonschema:"Monthly price in US cents."`
	AnnualPriceCents int64  `json:"annualPriceCents" jsonschema:"Annual price in US cents."`
	TrialDays        int    `json:"trialDays"`
}

// stepInput is one submitted setup step. Only the fields of Step apply.
type stepInput struct {
	Step        string `json:"step" jsonschema:"Step to save: account-information, payment-method, or project."`
	DisplayName string `json:"displayName,omitempty" jsonschema:"account-information: your display name."`
	Bypass      bool   `json:"continueOnFree,omitempty" jsonschema:"payment-method: continue on the Free plan without payment."`
	Plan        string `json:"plan,omitempty" jsonschema:"payment-method: paid plan key to check out, such as starter, pro, or business."`
	Interval    string `json:"interval,omitempty" jsonschema:"payment-method: billing interval, monthly or annual."`
	Name        string `json:"name,omitempty" jsonschema:"project: project name, up to 100 characters."`
	Region      string `json:"region,omitempty" jsonschema:"project: region code GRU, SFO, or FRA."`
}

type stepResult struct {
	Completed   bool   `json:"completed"`
	Step        string `json:"step" jsonschema:"Step to complete next, empty once setup is complete."`
	CheckoutURL string `json:"checkoutUrl,omitempty" jsonschema:"URL to open in a browser to finish payment checkout: a dashboard path in the demo, a Stripe Checkout URL live."`
}

// stepError is a validation failure whose text is shown to the user as is.
type stepError string

func (e stepError) Error() string        { return string(e) }
func (e stepError) Is(target error) bool { return target == core.ErrInvalid }

// Status returns the account's setup progress and the paid plans on offer.
func (s Service) Status(ctx context.Context, p core.Principal) (status, error) {
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return status{}, err
	}
	progress := accountProgress(account)
	result := status{Completed: progress.Completed, Step: progress.Step, Steps: steps(account.OnboardingStep), Plans: []planView{}}
	plans, err := paidPlans(ctx, s.deps.Store)
	if err != nil {
		return status{}, err
	}
	for _, plan := range plans {
		result.Plans = append(result.Plans, planView{
			Key: plan.Key, Name: plan.Name, PriceCents: plan.PriceCents, AnnualPriceCents: plan.AnnualPriceCents, TrialDays: plan.TrialDays,
		})
	}
	return result, nil
}

// ConfirmPayment advances the payment step once the account's onboarding
// checkout has completed, and leaves it unchanged otherwise.
func (s Service) ConfirmPayment(ctx context.Context, p core.Principal) error {
	err := s.deps.Store.UpdateAs(ctx, p, func(current *core.Account) error {
		if current.OnboardingStep != "payment-method" {
			return core.ErrConflict
		}
		organization, ok := current.Organizations[p.OrganizationID]
		if !ok || organization.Kind != "personal" || !organization.PaymentMethodValidated {
			return core.ErrForbidden
		}
		for _, checkout := range organization.CheckoutSessions {
			if checkout.OwnerUserID == p.UserID && checkout.OrganizationID == organization.ID &&
				checkout.Status == "complete" && checkout.ReturnPath == "/onboarding" {
				current.OnboardingStep = "project"
				return nil
			}
		}
		return core.ErrForbidden
	})
	if errors.Is(err, core.ErrForbidden) || errors.Is(err, core.ErrConflict) {
		return nil
	}
	return err
}

// SaveStep saves one setup step. A paid payment step returns the checkout
// the user must finish in a browser.
func (s Service) SaveStep(ctx context.Context, p core.Principal, in stepInput) (stepResult, error) {
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return stepResult{}, err
	}
	if account.OnboardingCompleted {
		return stepResult{Completed: true}, nil
	}
	var checkoutURL string
	switch in.Step {
	case "account-information":
		err = s.saveName(ctx, p, in.DisplayName)
	case "payment-method":
		checkoutURL, err = s.savePayment(ctx, p, account, in)
	case "project":
		err = s.saveProject(ctx, p, account, in.Name, in.Region)
	default:
		err = stepError("Choose a valid setup step.")
	}
	if err != nil {
		return stepResult{}, err
	}
	account, err = s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return stepResult{}, err
	}
	result := accountProgress(account)
	result.CheckoutURL = checkoutURL
	return result, nil
}

func accountProgress(account core.Account) stepResult {
	result := stepResult{Completed: account.OnboardingCompleted, Step: account.OnboardingStep}
	if result.Completed {
		result.Step = ""
	}
	return result
}

func (s Service) saveName(ctx context.Context, p core.Principal, name string) error {
	name = strings.TrimSpace(name)
	switch {
	case name == "":
		return stepError("This field is required.")
	case !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl):
		return stepError("Enter a valid name.")
	case utf8.RuneCountInString(name) > 128:
		return stepError("Use a name with 128 characters or fewer.")
	}
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		account.User.Name = name
		if account.OnboardingStep == "account-information" {
			account.OnboardingStep = "payment-method"
		}
		return nil
	})
}

func (s Service) savePayment(ctx context.Context, p core.Principal, account core.Account, in stepInput) (string, error) {
	if account.OnboardingStep != "payment-method" {
		return "", stepError("Complete the current setup step first.")
	}
	if in.Bypass {
		return "", s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
			if account.OnboardingStep != "payment-method" {
				return core.ErrConflict
			}
			account.OnboardingStep = "project"
			return nil
		})
	}
	if organization, ok := account.Organizations[p.OrganizationID]; !ok || organization.Kind != "personal" {
		return "", stepError("Return to your Personal workspace to finish account setup.")
	}
	checkoutURL, err := s.billing.StartCheckout(ctx, p, in.Plan, in.Interval, "/onboarding")
	if errors.Is(err, subscriptions.ErrPlanUnavailable) || errors.Is(err, core.ErrInvalid) {
		return "", stepError("Choose an available plan and billing interval.")
	}
	return checkoutURL, err
}

func (s Service) saveProject(ctx context.Context, p core.Principal, account core.Account, name, region string) error {
	name = strings.TrimSpace(name)
	switch {
	case account.OnboardingStep != "project":
		return stepError("Complete the current setup step first.")
	case !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl):
		return stepError("Enter a valid project name.")
	case utf8.RuneCountInString(name) > 100:
		return stepError("Use a project name with 100 characters or fewer.")
	}
	region = normalizedRegion(region)
	if region == "" {
		return stepError("Choose a supported project region.")
	}
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		organization, ok := account.Organizations[p.OrganizationID]
		if !ok {
			return core.ErrNotFound
		}
		project, ok := organization.Projects[p.ProjectID]
		if !ok || project.OrganizationID != organization.ID {
			return core.ErrNotFound
		}
		if name != "" {
			project.Name = name
		}
		project.Region = region
		project.Regions = []string{region}
		organization.Projects[project.ID] = project
		account.Organizations[organization.ID] = organization
		account.OnboardingStep = ""
		account.OnboardingCompleted = true
		return nil
	})
}

// paidPlans lists the visible paid plans in tier order.
func paidPlans(ctx context.Context, store core.Store) ([]core.Plan, error) {
	catalog, err := store.Plans(ctx)
	if err != nil {
		return nil, err
	}
	var plans []core.Plan
	for _, plan := range catalog {
		if plan.Visible && plan.PriceCents > 0 {
			plans = append(plans, plan)
		}
	}
	planRank := map[string]int{"starter": 0, "pro": 1, "business": 2}
	rank := func(plan core.Plan) int {
		if value, known := planRank[plan.Key]; known {
			return value
		}
		return len(planRank)
	}
	slices.SortFunc(plans, func(a, b core.Plan) int {
		return cmp.Or(cmp.Compare(rank(a), rank(b)), cmp.Compare(a.Key, b.Key))
	})
	return plans, nil
}

func normalizedRegion(region string) string {
	switch region = strings.ToUpper(strings.TrimSpace(region)); region {
	case "GRU", "SFO", "FRA":
		return region
	default:
		return ""
	}
}
