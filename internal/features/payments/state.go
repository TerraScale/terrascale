package payments

import (
	"context"
	"errors"
	"slices"
	"strconv"
	"sync"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// StatusNone marks a customer without any Stripe subscription.
const StatusNone = "none"

var (
	// ErrStaleState reports a newer saved or projected state; re-read Stripe and retry.
	ErrStaleState = errors.New("Stripe billing state changed during sync")
	// ErrUnknownCustomer reports a Stripe customer no organization is bound to.
	ErrUnknownCustomer = errors.New("Stripe customer is not bound to an organization")
)

type PaymentMethod struct {
	Brand    string
	Last4    string
	ExpMonth int64
	ExpYear  int64
}

// CustomerState is the complete billing state of one Stripe customer, read
// from Stripe by SyncCustomer and stored whole. It is never patched from
// webhook payloads, so it cannot drift from Stripe when events arrive out of
// order.
type CustomerState struct {
	CustomerID         string
	OrganizationID     string
	SubscriptionID     string
	Status             string
	PriceID            string
	PlanKey            string
	Interval           string
	CurrentPeriodStart time.Time
	CurrentPeriodEnd   time.Time
	CancelAtPeriodEnd  bool
	PendingPlanKey     string
	PendingInterval    string
	ScheduleID         string
	ScheduledPlanKey   string
	ScheduledInterval  string
	TrialUsed          bool
	PaymentMethod      *PaymentMethod
	Invoices           []core.Invoice
	Revision           uint64
	SyncedAt           time.Time
}

// StateStore persists the customer ↔ organization binding and each
// customer's synced state. Live mode stores them in TerraBase.
type StateStore interface {
	// BindCustomer records the organization's Stripe customer once and
	// returns the customer that won when two requests race.
	BindCustomer(ctx context.Context, organizationID, customerID string) (string, error)
	CustomerFor(ctx context.Context, organizationID string) (string, bool, error)
	OrganizationFor(ctx context.Context, customerID string) (string, bool, error)
	// Load returns the stored state and its version; an unknown customer
	// returns an empty version.
	Load(ctx context.Context, customerID string) (CustomerState, string, error)
	// Save replaces the state only if it is still at version, or creates it
	// when version is empty. Otherwise it returns ErrStaleState.
	Save(ctx context.Context, state CustomerState, version string) error
}

// memoryStateStore implements StateStore in process memory for tests.
type memoryStateStore struct {
	mu            sync.Mutex
	customerByOrg map[string]string
	orgByCustomer map[string]string
	states        map[string]versionedState
}

type versionedState struct {
	state   CustomerState
	version int
}

func newMemoryStateStore() *memoryStateStore {
	return &memoryStateStore{
		customerByOrg: make(map[string]string),
		orgByCustomer: make(map[string]string),
		states:        make(map[string]versionedState),
	}
}

func (store *memoryStateStore) BindCustomer(_ context.Context, organizationID, customerID string) (string, error) {
	store.mu.Lock()
	defer store.mu.Unlock()
	if existing, ok := store.customerByOrg[organizationID]; ok {
		return existing, nil
	}
	if _, taken := store.orgByCustomer[customerID]; taken {
		return "", core.ErrConflict
	}
	store.customerByOrg[organizationID] = customerID
	store.orgByCustomer[customerID] = organizationID
	store.states[customerID] = versionedState{
		state: CustomerState{CustomerID: customerID, OrganizationID: organizationID, Status: StatusNone}, version: 1,
	}
	return customerID, nil
}

func (store *memoryStateStore) CustomerFor(_ context.Context, organizationID string) (string, bool, error) {
	store.mu.Lock()
	defer store.mu.Unlock()
	customerID, ok := store.customerByOrg[organizationID]
	return customerID, ok, nil
}

func (store *memoryStateStore) OrganizationFor(_ context.Context, customerID string) (string, bool, error) {
	store.mu.Lock()
	defer store.mu.Unlock()
	organizationID, ok := store.orgByCustomer[customerID]
	return organizationID, ok, nil
}

func (store *memoryStateStore) Load(_ context.Context, customerID string) (CustomerState, string, error) {
	store.mu.Lock()
	defer store.mu.Unlock()
	stored, ok := store.states[customerID]
	if !ok {
		return CustomerState{}, "", nil
	}
	return cloneState(stored.state), strconv.Itoa(stored.version), nil
}

func (store *memoryStateStore) Save(_ context.Context, state CustomerState, version string) error {
	store.mu.Lock()
	defer store.mu.Unlock()
	current, exists := store.states[state.CustomerID]
	if (version == "" && exists) || (version != "" && (!exists || strconv.Itoa(current.version) != version)) {
		return ErrStaleState
	}
	store.states[state.CustomerID] = versionedState{state: cloneState(state), version: current.version + 1}
	return nil
}

func cloneState(state CustomerState) CustomerState {
	state.Invoices = slices.Clone(state.Invoices)
	if state.PaymentMethod != nil {
		method := *state.PaymentMethod
		state.PaymentMethod = &method
	}
	return state
}
