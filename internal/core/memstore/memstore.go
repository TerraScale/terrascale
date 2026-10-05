// Package memstore keeps the dashboard's state in process memory. It is the
// core.Store of the TS_DASHBOARD_PRODUCTION_DEMO deployment and of the test
// suites. One lock serializes every change, which is what lets it enforce the
// cross-aggregate invariants listed in docs/store-contract.md with full scans.
package memstore

import (
	"context"
	"reflect"
	"slices"
	"strings"
	"sync"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// Store is one process's demo state. Updates commit an isolated copy under a
// lock, so failed callbacks cannot publish partial mutations.
//
// Organizations live only in organizations; an entry in accounts records the
// account's own fields and the IDs of the organizations it belongs to, so
// shared tenants are stored and copied once.
type Store struct {
	mu            sync.RWMutex
	accounts      map[string]core.Account
	byEmail       map[string]string
	bySubject     map[string]string
	organizations map[string]core.Organization
	byAccessToken map[string]string
	plans         map[string]core.Plan
	adminActions  []core.AdminAction
}

var _ core.Store = (*Store)(nil)

// New returns an empty store with core.DefaultPlans.
func New() *Store {
	plans := make(map[string]core.Plan)
	for _, plan := range core.DefaultPlans() {
		plans[plan.ID] = plan
	}
	return &Store{
		accounts: make(map[string]core.Account), byEmail: make(map[string]string), bySubject: make(map[string]string),
		organizations: make(map[string]core.Organization), byAccessToken: make(map[string]string),
		plans: plans,
	}
}

func (s *Store) CreateAccount(ctx context.Context, user core.User) (core.Account, error) {
	a, err := core.NewAccount(user)
	if err != nil {
		return core.Account{}, err
	}
	if err := s.InsertAccount(ctx, a); err != nil {
		return core.Account{}, err
	}
	return core.CloneAccount(a), nil
}

// InsertAccount loads a complete fixture. It enforces unique account and email
// identities and never retains caller-owned maps or slices.
func (s *Store) InsertAccount(_ context.Context, a core.Account) error {
	a.User.Email = core.NormalizeEmail(a.User.Email)
	if a.ID == "" || a.User.ID == "" || a.User.Email == "" {
		return core.ErrInvalid
	}
	if a.Organizations == nil {
		a.Organizations = make(map[string]core.Organization)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.accounts[a.ID]; ok {
		return core.ErrConflict
	}
	if _, ok := s.byEmail[a.User.Email]; ok {
		return core.ErrConflict
	}
	if _, ok := s.bySubject[a.User.AuthSubject]; ok {
		return core.ErrConflict
	}
	for id := range a.Organizations {
		if _, exists := s.organizations[id]; exists {
			return core.ErrConflict
		}
	}
	s.putLocked(a)
	s.byEmail[a.User.Email] = a.ID
	if a.User.AuthSubject != "" {
		s.bySubject[a.User.AuthSubject] = a.ID
	}
	return nil
}

func (s *Store) FindByEmail(_ context.Context, email string) (core.Account, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[s.byEmail[core.NormalizeEmail(email)]]
	if !ok {
		return core.Account{}, core.ErrNotFound
	}
	return s.materialize(a), nil
}

func (s *Store) FindBySubject(_ context.Context, subject string) (core.Account, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[s.bySubject[subject]]
	if !ok {
		return core.Account{}, core.ErrNotFound
	}
	return s.materialize(a), nil
}

func (s *Store) SnapshotAs(_ context.Context, p core.Principal) (core.Account, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return core.Account{}, core.ErrNotFound
	}
	if err := core.AuthorizeAccount(s.viewLocked(a), p); err != nil {
		return core.Account{}, err
	}
	return s.materialize(a), nil
}

// ViewAs calls read with the principal's authorized account while holding the
// read lock. The account shares the store's maps and slices, so read must not
// retain or modify anything reachable from it.
func (s *Store) ViewAs(_ context.Context, p core.Principal, read func(core.Account) error) error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return core.ErrNotFound
	}
	view := s.viewLocked(a)
	if err := core.AuthorizeAccount(view, p); err != nil {
		return err
	}
	return read(view)
}

func (s *Store) ScopedPrincipal(_ context.Context, p core.Principal, scope core.Scope) (core.Principal, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return core.Principal{}, core.ErrUnauthorized
	}
	return core.ScopePrincipal(s.viewLocked(a), p, scope)
}

// CurrentPrincipal returns the account's principal for its current selection
// when the account still belongs to userID.
func (s *Store) CurrentPrincipal(_ context.Context, accountID, userID string) (core.Principal, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[accountID]
	if !ok || a.User.ID != userID {
		return core.Principal{}, core.ErrNotFound
	}
	return s.viewLocked(a).Principal(), nil
}

// SessionPrincipal resolves a session's principal for the credential version
// it was issued with, without copying the account.
func (s *Store) SessionPrincipal(_ context.Context, accountID, userID string, authVersion uint64) (core.Principal, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[accountID]
	if !ok || a.User.ID != userID || a.AuthVersion != authVersion {
		return core.Principal{}, core.ErrUnauthorized
	}
	view := s.viewLocked(a)
	p := view.Principal()
	if core.AuthorizeAccount(view, p) != nil {
		return core.Principal{}, core.ErrUnauthorized
	}
	return p, nil
}

// Update is for authentication and trusted server integration. HTTP feature
// handlers should use UpdateAs, which rechecks principal scope under this lock.
func (s *Store) Update(_ context.Context, accountID string, mutate func(*core.Account) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.updateLocked(accountID, mutate)
}

func (s *Store) UpdateAs(_ context.Context, p core.Principal, mutate func(*core.Account) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.updateAsLocked(p, mutate)
}

// ChangeEmailAs updates the account email and its lookup index in one scoped
// transaction. The expected hash binds the change to the password verified by
// the caller even if that credential changes between verification and commit.
func (s *Store) ChangeEmailAs(_ context.Context, p core.Principal, email, expectedPasswordHash string) error {
	email = core.NormalizeEmail(email)
	if email == "" || expectedPasswordHash == "" {
		return core.ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.accounts[p.AccountID]
	if !ok {
		return core.ErrNotFound
	}
	if err := core.AuthorizeAccount(s.viewLocked(current), p); err != nil {
		return err
	}
	next := s.materialize(current)
	if next.User.PasswordHash != expectedPasswordHash {
		return core.ErrConflict
	}
	if accountID, exists := s.byEmail[email]; exists && accountID != next.ID {
		return core.ErrConflict
	}
	oldEmail := next.User.Email
	if oldEmail == email {
		return nil
	}
	next.User.Email = email
	core.SyncMemberships(&next)
	s.putLocked(next)
	delete(s.byEmail, oldEmail)
	s.byEmail[email] = next.ID
	return nil
}

// UpdateAsWithPlan resolves a canonical plan under the same lock as the scoped
// account update. A caller can validate the plan and commit its account change
// without a plan edit or deletion occurring between those operations.
func (s *Store) UpdateAsWithPlan(_ context.Context, p core.Principal, planID string, mutate func(*core.Account, core.Plan) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.updateAsLocked(p, func(account *core.Account) error {
		plan, ok := s.plans[planID]
		if !ok {
			return core.ErrNotFound
		}
		return mutate(account, plan)
	})
}

func (s *Store) updateAsLocked(p core.Principal, mutate func(*core.Account) error) error {
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return core.ErrNotFound
	}
	if err := core.AuthorizeAccount(s.viewLocked(a), p); err != nil {
		return err
	}
	// The committed organizations are the state mutate started from, so they
	// serve as the "before" image without copying the account again.
	existed := func(id string) bool {
		_, member := a.Organizations[id]
		_, live := s.organizations[id]
		return member && live
	}
	return s.updateLocked(p.AccountID, func(next *core.Account) error {
		if err := mutate(next); err != nil {
			return err
		}
		for id := range a.Organizations {
			old, live := s.organizations[id]
			if id == p.OrganizationID || !live {
				continue
			}
			updated, ok := next.Organizations[id]
			if !ok || !reflect.DeepEqual(old, updated) {
				return core.ErrForbidden
			}
		}
		for id, added := range next.Organizations {
			if existed(id) {
				continue
			}
			member, ok := added.Members[p.UserID]
			if added.ID != id || added.OwnerUserID != p.UserID || !ok || member.Role != "owner" {
				return core.ErrForbidden
			}
		}
		return nil
	})
}

func (s *Store) updateLocked(accountID string, mutate func(*core.Account) error) error {
	current, ok := s.accounts[accountID]
	if !ok {
		return core.ErrNotFound
	}
	next := s.materialize(current)
	if err := mutate(&next); err != nil {
		return err
	}
	if next.ID != current.ID || next.User.ID != current.User.ID || core.NormalizeEmail(next.User.Email) != current.User.Email {
		return core.ErrInvalid
	}
	if next.User.PasswordHash == "" {
		return core.ErrInvalid
	}
	linked := next.User.AuthSubject != current.User.AuthSubject
	if linked {
		// A subject is set once, to one not linked to another account.
		if current.User.AuthSubject != "" {
			return core.ErrInvalid
		}
		if _, taken := s.bySubject[next.User.AuthSubject]; taken {
			return core.ErrConflict
		}
	}
	core.SyncMemberships(&next)
	if next.User.PasswordHash != current.User.PasswordHash {
		// A credential change ends every personal access token along with
		// the sessions the new AuthVersion invalidates.
		next.AuthVersion = current.AuthVersion + 1
		next.AccessTokens = nil
	} else {
		next.AuthVersion = current.AuthVersion
	}
	for id, org := range next.Organizations {
		if !s.providerUniqueLocked(id, org) {
			return core.ErrConflict
		}
		if !s.withinDatabaseLimitLocked(id, org) {
			return core.ErrDatabaseLimit
		}
	}
	s.putLocked(next)
	if linked {
		s.bySubject[next.User.AuthSubject] = next.ID
	}
	s.repairSelectionsLocked()
	return nil
}

// withinDatabaseLimitLocked reports whether org may be committed with its
// databases. Whatever route adds a database, the organization must stay
// within its plan's MaxDatabases; one already past it, such as after a
// downgrade, keeps what it has but cannot gain more.
func (s *Store) withinDatabaseLimitLocked(id string, org core.Organization) bool {
	limit := s.plans[org.PlanID].MaxDatabases
	count := org.DatabaseCount()
	return limit <= 0 || count <= limit || count <= s.organizations[id].DatabaseCount()
}

func (s *Store) providerUniqueLocked(id string, org core.Organization) bool {
	for otherID, other := range s.organizations {
		if otherID == id {
			continue
		}
		if org.Billing.CustomerID != "" && other.Billing.CustomerID == org.Billing.CustomerID {
			return false
		}
		if org.Billing.SubscriptionID != "" && other.Billing.SubscriptionID == org.Billing.SubscriptionID {
			return false
		}
	}
	return true
}

func (s *Store) repairSelectionsLocked() {
	for accountID, a := range s.accounts {
		if a.ActiveOrganizationID == "" {
			continue
		}
		changed := false
		for id := range a.Organizations {
			org, ok := s.organizations[id]
			if !ok || !core.MemberActive(org, a.User.ID) {
				delete(a.Organizations, id)
				changed = true
			}
		}
		if _, ok := a.Organizations[a.ActiveOrganizationID]; !ok {
			a.ActiveOrganizationID = ""
			a.ActiveProjectID = ""
			for id := range a.Organizations {
				if s.organizations[id].Kind == "personal" {
					a.ActiveOrganizationID = id
					break
				}
			}
			if a.ActiveOrganizationID == "" {
				for id := range a.Organizations {
					a.ActiveOrganizationID = id
					break
				}
			}
			a.ActiveProjectID = s.organizations[a.ActiveOrganizationID].DefaultProjectID()
			changed = true
		}
		// A project another member deleted must not stay selected: the
		// session and every later sign-in would fail to authorize it.
		if org, ok := s.organizations[a.ActiveOrganizationID]; ok && a.ActiveProjectID != "" {
			if project, exists := org.Projects[a.ActiveProjectID]; !exists || project.OrganizationID != org.ID {
				a.ActiveProjectID = org.DefaultProjectID()
				changed = true
			}
		}
		if changed {
			s.accounts[accountID] = detachAccount(a)
		}
	}
}

// putLocked commits a's own fields and memberships to accounts, a copy of
// each of its organizations to organizations, and its access token digests
// to the token index.
func (s *Store) putLocked(a core.Account) {
	for _, token := range s.accounts[a.ID].AccessTokens {
		delete(s.byAccessToken, token.SecretHash)
	}
	for _, token := range a.AccessTokens {
		s.byAccessToken[token.SecretHash] = a.ID
	}
	s.accounts[a.ID] = detachAccount(a)
	for id, org := range a.Organizations {
		s.organizations[id] = core.CloneOrganization(org)
	}
}

// materialize returns an isolated copy of a stored account with its live
// organizations.
func (s *Store) materialize(a core.Account) core.Account {
	n := core.CloneAccountFields(a)
	n.Organizations = make(map[string]core.Organization, len(a.Organizations))
	for id := range a.Organizations {
		if org, ok := s.organizations[id]; ok {
			n.Organizations[id] = core.CloneOrganization(org)
		}
	}
	return n
}

// viewLocked is materialize without copies, for read-only use under the lock.
func (s *Store) viewLocked(a core.Account) core.Account {
	n := a
	n.Organizations = make(map[string]core.Organization, len(a.Organizations))
	for id := range a.Organizations {
		if org, ok := s.organizations[id]; ok {
			n.Organizations[id] = org
		}
	}
	return n
}

// detachAccount copies a's own fields and keeps only its organization IDs.
func detachAccount(a core.Account) core.Account {
	n := core.CloneAccountFields(a)
	n.Organizations = make(map[string]core.Organization, len(a.Organizations))
	for id := range a.Organizations {
		n.Organizations[id] = core.Organization{ID: id}
	}
	return n
}

// CreateAccessToken issues a personal access token for the principal's user
// and returns it with its one-time secret.
func (s *Store) CreateAccessToken(ctx context.Context, p core.Principal, name string, lifetime time.Duration) (core.AccessToken, string, error) {
	now := time.Now().UTC()
	token, secret, err := core.NewAccessToken(name, lifetime, now)
	if err != nil {
		return core.AccessToken{}, "", err
	}
	if err := s.UpdateAs(ctx, p, func(a *core.Account) error { return core.AddAccessToken(a, token, now) }); err != nil {
		return core.AccessToken{}, "", err
	}
	return token, secret, nil
}

// RevokeAccessToken deletes one of the principal's personal access tokens.
func (s *Store) RevokeAccessToken(ctx context.Context, p core.Principal, id string) error {
	return s.UpdateAs(ctx, p, func(a *core.Account) error { return core.RemoveAccessToken(a, id) })
}

// AccessTokenPrincipal resolves a personal access token secret to its
// account's principal for the account's current selection.
func (s *Store) AccessTokenPrincipal(_ context.Context, secret string) (core.Principal, error) {
	if !strings.HasPrefix(secret, core.AccessTokenPrefix) {
		return core.Principal{}, core.ErrUnauthorized
	}
	hash := core.HashAccessToken(secret)
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[s.byAccessToken[hash]]
	if !ok {
		return core.Principal{}, core.ErrUnauthorized
	}
	index := slices.IndexFunc(a.AccessTokens, func(t core.AccessToken) bool { return t.SecretHash == hash })
	if index < 0 || a.AccessTokens[index].Expired(time.Now()) {
		return core.Principal{}, core.ErrUnauthorized
	}
	view := s.viewLocked(a)
	p := view.Principal()
	if err := core.AuthorizeAccount(view, p); err != nil {
		return core.Principal{}, core.ErrUnauthorized
	}
	return p, nil
}
