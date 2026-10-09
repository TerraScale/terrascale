package payments

import (
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"net/http"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/sdk/v1"
)

const (
	customersCollection     = "stripe_customers"
	organizationsCollection = "stripe_organizations"
)

// terraBaseStateStore uses the dashboard's dedicated database. Bindings
// commit atomically; synced customer state uses the loaded document version.
type terraBaseStateStore struct{ db *v1.Database }

// customerDocument is the stored form of CustomerState. Times are Unix
// seconds and every field is declared in the collection schema.
type customerDocument struct {
	OrganizationID     string         `json:"organizationId"`
	SubscriptionID     string         `json:"subscriptionId"`
	Status             string         `json:"status"`
	PriceID            string         `json:"priceId"`
	PlanKey            string         `json:"planKey"`
	Interval           string         `json:"interval"`
	CurrentPeriodStart int64          `json:"currentPeriodStart"`
	CurrentPeriodEnd   int64          `json:"currentPeriodEnd"`
	CancelAtPeriodEnd  bool           `json:"cancelAtPeriodEnd"`
	PendingPlanKey     string         `json:"pendingPlanKey"`
	PendingInterval    string         `json:"pendingInterval"`
	ScheduleID         string         `json:"scheduleId"`
	ScheduledPlanKey   string         `json:"scheduledPlanKey"`
	ScheduledInterval  string         `json:"scheduledInterval"`
	TrialUsed          bool           `json:"trialUsed"`
	PaymentBrand       string         `json:"paymentBrand"`
	PaymentLast4       string         `json:"paymentLast4"`
	PaymentExpMonth    int64          `json:"paymentExpMonth"`
	PaymentExpYear     int64          `json:"paymentExpYear"`
	Invoices           []core.Invoice `json:"invoices"`
	SyncedAt           int64          `json:"syncedAt"`
}

type organizationDocument struct {
	CustomerID string `json:"customerId"`
}

func newTerraBaseStateStore(db *v1.Database) *terraBaseStateStore {
	return &terraBaseStateStore{db: db}
}

func billingID(collection, key string) string { return v1.DocumentIDFor(collection, key) }

func (store *terraBaseStateStore) BindCustomer(ctx context.Context, organizationID, customerID string) (string, error) {
	if organizationID == "" || customerID == "" {
		return "", ErrInvalidBillingInput
	}
	existing, found, err := store.CustomerFor(ctx, organizationID)
	if err != nil {
		return "", err
	}
	if found {
		owner, bound, err := store.OrganizationFor(ctx, existing)
		if err != nil {
			return "", err
		}
		if !bound || owner != organizationID {
			return "", core.ErrConflict
		}
		return existing, nil
	}
	_, err = store.db.Tx().
		Create(organizationsCollection, billingID(organizationsCollection, organizationID), organizationDocument{CustomerID: customerID}).
		Create(customersCollection, billingID(customersCollection, customerID), customerDocument{OrganizationID: organizationID, Status: StatusNone}).Commit(ctx)
	if err == nil {
		return customerID, nil
	}
	if isConflict(err) {
		// A confirmed precondition failure committed nothing. Resolve the winner.
		existing, found, lookupErr := store.CustomerFor(ctx, organizationID)
		if lookupErr != nil {
			return "", lookupErr
		}
		if found {
			return existing, nil
		}
		return "", core.ErrConflict
	}
	return "", fmt.Errorf("%w: bind Stripe customer: %w", core.ErrUnavailable, err)
}

func (store *terraBaseStateStore) CustomerFor(ctx context.Context, organizationID string) (string, bool, error) {
	var document organizationDocument
	found, _, err := store.get(ctx, organizationsCollection, organizationID, &document)
	return document.CustomerID, found && document.CustomerID != "", err
}

func (store *terraBaseStateStore) OrganizationFor(ctx context.Context, customerID string) (string, bool, error) {
	var document customerDocument
	found, _, err := store.get(ctx, customersCollection, customerID, &document)
	return document.OrganizationID, found && document.OrganizationID != "", err
}

func (store *terraBaseStateStore) Load(ctx context.Context, customerID string) (CustomerState, string, error) {
	var document customerDocument
	found, version, err := store.get(ctx, customersCollection, customerID, &document)
	if err != nil || !found {
		return CustomerState{}, "", err
	}
	return document.state(customerID), version, nil
}

func (store *terraBaseStateStore) Save(ctx context.Context, state CustomerState, version string) error {
	condition := v1.IfNoneMatchAny()
	if version != "" {
		condition = v1.IfMatch(version)
	}
	_, err := store.db.Collection(customersCollection).Put(ctx, billingID(customersCollection, state.CustomerID), documentFromState(state), condition)
	if isConflict(err) {
		return ErrStaleState
	}
	if err != nil {
		return fmt.Errorf("%w: save Stripe state: %w", core.ErrUnavailable, err)
	}
	return nil
}

func (store *terraBaseStateStore) get(ctx context.Context, collection, id string, target any) (bool, string, error) {
	document, err := store.db.Collection(collection).Get(ctx, billingID(collection, id))
	if errors.Is(err, v1.ErrNotFound) {
		return false, "", nil
	}
	if err != nil {
		return false, "", fmt.Errorf("%w: read Stripe state: %w", core.ErrUnavailable, err)
	}
	if document.State != v1.StateValue {
		return false, "", core.ErrUnavailable
	}
	if err := json.Unmarshal(document.Document, target); err != nil {
		return false, "", fmt.Errorf("%w: decode %s/%s: %w", core.ErrUnavailable, collection, id, err)
	}
	return true, document.Version, nil
}

func isConflict(err error) bool {
	apiError, ok := errors.AsType[*v1.APIError](err)
	return ok && (apiError.Status == http.StatusConflict || apiError.Status == http.StatusPreconditionFailed)
}

func documentFromState(state CustomerState) customerDocument {
	document := customerDocument{
		OrganizationID: state.OrganizationID, SubscriptionID: state.SubscriptionID, Status: state.Status,
		PriceID: state.PriceID, PlanKey: state.PlanKey, Interval: state.Interval,
		CurrentPeriodStart: unixSeconds(state.CurrentPeriodStart), CurrentPeriodEnd: unixSeconds(state.CurrentPeriodEnd),
		CancelAtPeriodEnd: state.CancelAtPeriodEnd, PendingPlanKey: state.PendingPlanKey, PendingInterval: state.PendingInterval,
		ScheduleID: state.ScheduleID, ScheduledPlanKey: state.ScheduledPlanKey, ScheduledInterval: state.ScheduledInterval,
		TrialUsed: state.TrialUsed, Invoices: state.Invoices, SyncedAt: unixSeconds(state.SyncedAt),
	}
	if method := state.PaymentMethod; method != nil {
		document.PaymentBrand, document.PaymentLast4 = method.Brand, method.Last4
		document.PaymentExpMonth, document.PaymentExpYear = method.ExpMonth, method.ExpYear
	}
	return document
}

func (document customerDocument) state(customerID string) CustomerState {
	state := CustomerState{
		CustomerID: customerID, OrganizationID: document.OrganizationID, SubscriptionID: document.SubscriptionID,
		Status: document.Status, PriceID: document.PriceID, PlanKey: document.PlanKey, Interval: document.Interval,
		CurrentPeriodStart: fromUnix(document.CurrentPeriodStart), CurrentPeriodEnd: fromUnix(document.CurrentPeriodEnd),
		CancelAtPeriodEnd: document.CancelAtPeriodEnd, PendingPlanKey: document.PendingPlanKey, PendingInterval: document.PendingInterval,
		ScheduleID: document.ScheduleID, ScheduledPlanKey: document.ScheduledPlanKey, ScheduledInterval: document.ScheduledInterval,
		TrialUsed: document.TrialUsed, Invoices: document.Invoices, SyncedAt: fromUnix(document.SyncedAt),
	}
	if document.PaymentLast4 != "" {
		state.PaymentMethod = &PaymentMethod{
			Brand: document.PaymentBrand, Last4: document.PaymentLast4,
			ExpMonth: document.PaymentExpMonth, ExpYear: document.PaymentExpYear,
		}
	}
	return state
}

func unixSeconds(value time.Time) int64 {
	if value.IsZero() {
		return 0
	}
	return value.Unix()
}

func fromUnix(seconds int64) time.Time {
	if seconds <= 0 {
		return time.Time{}
	}
	return time.Unix(seconds, 0).UTC()
}
