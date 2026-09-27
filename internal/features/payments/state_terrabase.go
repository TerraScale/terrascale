package payments

import (
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"net/http"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

const (
	customersCollection     = "stripe_customers"
	organizationsCollection = "stripe_organizations"
)

// terraBaseStateStore keeps Stripe billing state in the dashboard's own
// TerraBase database through ts-go. Documents are keyed by Stripe customer ID
// and organization ID, and every write is conditional so concurrent syncs and
// customer bindings cannot overwrite each other.
type terraBaseStateStore struct {
	client     *sdk.Client
	databaseID string
}

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

func newTerraBaseStateStore(client *sdk.Client, databaseID string) *terraBaseStateStore {
	return &terraBaseStateStore{client: client, databaseID: databaseID}
}

// EnsureSchema declares both collections. Stripe customer and organization
// IDs are unique across bindings, so one customer can never serve two tenants.
func (store *terraBaseStateStore) EnsureSchema(ctx context.Context) error {
	customers, err := sdk.NewCollection(customersCollection).
		Field("organizationId", sdk.FieldString, sdk.Required(), sdk.Unique()).
		Field("subscriptionId", sdk.FieldString).
		Field("status", sdk.FieldString, sdk.Required()).
		Field("priceId", sdk.FieldString).
		Field("planKey", sdk.FieldString).
		Field("interval", sdk.FieldString).
		Field("currentPeriodStart", sdk.FieldInt).
		Field("currentPeriodEnd", sdk.FieldInt).
		Field("cancelAtPeriodEnd", sdk.FieldBool).
		Field("pendingPlanKey", sdk.FieldString).
		Field("pendingInterval", sdk.FieldString).
		Field("scheduleId", sdk.FieldString).
		Field("scheduledPlanKey", sdk.FieldString).
		Field("scheduledInterval", sdk.FieldString).
		Field("trialUsed", sdk.FieldBool).
		Field("paymentBrand", sdk.FieldString).
		Field("paymentLast4", sdk.FieldString).
		Field("paymentExpMonth", sdk.FieldInt).
		Field("paymentExpYear", sdk.FieldInt).
		Field("invoices", sdk.FieldJSON).
		Field("syncedAt", sdk.FieldInt).
		Build()
	if err != nil {
		return err
	}
	organizations, err := sdk.NewCollection(organizationsCollection).
		Field("customerId", sdk.FieldString, sdk.Required(), sdk.Unique()).
		Build()
	if err != nil {
		return err
	}
	for _, schema := range []sdk.CollectionSchema{customers, organizations} {
		if _, err := store.client.Databases.PutCollectionSchema(ctx, store.databaseID, schema.Name, schema,
			sdk.WithIdempotencyKey("dashboard-schema:"+schema.Name)); err != nil {
			return fmt.Errorf("declare %s schema: %w", schema.Name, err)
		}
	}
	return nil
}

// BindCustomer writes the organization's binding first and the customer's
// reverse binding second. Both writes are create-only and an existing binding
// is accepted when it matches, so binding an already bound organization again
// restores a reverse binding an interrupted earlier call left missing.
func (store *terraBaseStateStore) BindCustomer(ctx context.Context, organizationID, customerID string) (string, error) {
	if organizationID == "" || customerID == "" {
		return "", ErrInvalidBillingInput
	}
	_, err := store.client.Documents.Create(ctx, store.databaseID, organizationsCollection, organizationID,
		organizationDocument{CustomerID: customerID}, sdk.WithIdempotencyKey("bind:"+organizationID+":"+customerID))
	if isConflict(err) {
		// Another request bound this organization first; its customer wins.
		existing, ok, lookupErr := store.CustomerFor(ctx, organizationID)
		if lookupErr != nil {
			return "", lookupErr
		}
		if !ok {
			return "", core.ErrConflict
		}
		customerID, err = existing, nil
	}
	if err != nil {
		return "", err
	}
	// The reverse binding starts empty and is filled by the first sync; its
	// create-only write fails if the customer already serves another tenant.
	_, err = store.client.Documents.Create(ctx, store.databaseID, customersCollection, customerID,
		customerDocument{OrganizationID: organizationID, Status: StatusNone},
		sdk.WithIdempotencyKey("bind-customer:"+customerID))
	if isConflict(err) {
		boundTo, ok, lookupErr := store.OrganizationFor(ctx, customerID)
		if lookupErr != nil {
			return "", lookupErr
		}
		if !ok || boundTo != organizationID {
			return "", core.ErrConflict
		}
		err = nil
	}
	if err != nil {
		return "", err
	}
	return customerID, nil
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
	found, etag, err := store.get(ctx, customersCollection, customerID, &document)
	if err != nil || !found {
		return CustomerState{}, "", err
	}
	return document.state(customerID), etag, nil
}

func (store *terraBaseStateStore) Save(ctx context.Context, state CustomerState, version string) error {
	condition := sdk.WithCreateOnly()
	if version != "" {
		condition = sdk.WithIfMatch(version)
	}
	_, err := store.client.Documents.Put(ctx, store.databaseID, customersCollection, state.CustomerID,
		documentFromState(state), condition, sdk.WithIdempotencyKey(core.NewID("sync")))
	if isConflict(err) {
		return ErrStaleState
	}
	return err
}

// get decodes one document, reporting false for a missing document.
func (store *terraBaseStateStore) get(ctx context.Context, collection, id string, target any) (bool, string, error) {
	document, err := store.client.Documents.Get(ctx, store.databaseID, collection, id)
	if apiError, ok := errors.AsType[*sdk.APIError](err); ok && apiError.Status == http.StatusNotFound {
		return false, "", nil
	}
	if err != nil {
		return false, "", err
	}
	if err := json.Unmarshal(document.Body, target); err != nil {
		return false, "", fmt.Errorf("decode %s/%s: %w", collection, id, err)
	}
	return true, document.ETag, nil
}

// isConflict reports a failed create-only or If-Match precondition.
func isConflict(err error) bool {
	apiError, ok := errors.AsType[*sdk.APIError](err)
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
