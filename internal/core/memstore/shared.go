package memstore

import (
	"context"
	"maps"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// FindInvitation returns a copy of a pending invitation without revealing the
// stored token hash or granting access to its organization.
func (s *Store) FindInvitation(_ context.Context, tokenHash string) (core.Organization, core.Invitation, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if tokenHash == "" {
		return core.Organization{}, core.Invitation{}, core.ErrNotFound
	}
	for _, org := range s.organizations {
		for _, invitation := range org.Invitations {
			if invitation.TokenHash == tokenHash && strings.EqualFold(invitation.Status, "pending") &&
				(invitation.ExpiresAt.IsZero() || time.Now().Before(invitation.ExpiresAt)) {
				invitation.TokenHash = ""
				return core.Organization{ID: org.ID, Name: org.Name, Kind: org.Kind}, invitation, nil
			}
		}
	}
	return core.Organization{}, core.Invitation{}, core.ErrNotFound
}

// AcceptInvitation joins one account to an existing canonical organization.
// The invite, email, membership, and selected scope change in one lock/commit.
func (s *Store) AcceptInvitation(_ context.Context, p core.Principal, tokenHash string) (core.Organization, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	a, ok := s.accounts[p.AccountID]
	if !ok || a.User.ID != p.UserID || tokenHash == "" {
		return core.Organization{}, core.ErrForbidden
	}
	for id, canonical := range s.organizations {
		for invitationID, invitation := range canonical.Invitations {
			if invitation.TokenHash != tokenHash {
				continue
			}
			if !strings.EqualFold(invitation.Status, "pending") ||
				(!invitation.ExpiresAt.IsZero() && !time.Now().Before(invitation.ExpiresAt)) {
				return core.Organization{}, core.ErrConflict
			}
			if core.NormalizeEmail(invitation.Email) != a.User.Email {
				return core.Organization{}, core.ErrForbidden
			}
			if !strings.EqualFold(canonical.Status, "active") || canonical.Archived || canonical.DeletionRequested {
				return core.Organization{}, core.ErrConflict
			}
			org := core.CloneOrganization(canonical)
			if plan, ok := s.plans[org.PlanID]; ok && plan.MaxMembers > 0 {
				active := 0
				for _, member := range org.Members {
					if member.Status == "active" {
						active++
					}
				}
				if active >= plan.MaxMembers {
					return core.Organization{}, core.ErrConflict
				}
			}
			if org.Members == nil {
				org.Members = make(map[string]core.Member)
			}
			org.Members[a.User.ID] = core.Member{
				UserID: a.User.ID, Email: a.User.Email, Name: a.User.Name,
				Role: invitation.Role, Status: "active", CreatedAt: time.Now().UTC(),
			}
			invitation.Status = "accepted"
			org.Invitations[invitationID] = invitation
			next := detachAccount(s.viewLocked(a))
			next.Organizations[id] = core.Organization{ID: id}
			next.ActiveOrganizationID = id
			next.ActiveProjectID = org.DefaultProjectID()
			s.organizations[id] = org
			s.accounts[a.ID] = next
			return core.CloneOrganization(org), nil
		}
	}
	return core.Organization{}, core.ErrNotFound
}

func (s *Store) DeclineInvitation(_ context.Context, p core.Principal, tokenHash string) (core.Organization, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	a, ok := s.accounts[p.AccountID]
	if !ok || a.User.ID != p.UserID || tokenHash == "" {
		return core.Organization{}, core.ErrForbidden
	}
	for id, canonical := range s.organizations {
		for invitationID, invitation := range canonical.Invitations {
			if invitation.TokenHash != tokenHash {
				continue
			}
			if !strings.EqualFold(invitation.Status, "pending") ||
				(!invitation.ExpiresAt.IsZero() && !time.Now().Before(invitation.ExpiresAt)) {
				return core.Organization{}, core.ErrConflict
			}
			if core.NormalizeEmail(invitation.Email) != a.User.Email {
				return core.Organization{}, core.ErrForbidden
			}
			org := core.CloneOrganization(canonical)
			invitation.Status = "declined"
			org.Invitations[invitationID] = invitation
			s.organizations[id] = org
			return core.CloneOrganization(org), nil
		}
	}
	return core.Organization{}, core.ErrNotFound
}

// Organization returns a copy of one organization for trusted server-side
// integrations that act without a signed-in principal, such as Stripe syncs.
func (s *Store) Organization(_ context.Context, organizationID string) (core.Organization, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	org, ok := s.organizations[organizationID]
	if !ok {
		return core.Organization{}, core.ErrNotFound
	}
	return core.CloneOrganization(org), nil
}

// UpdateOrganization applies a trusted server-side change to one
// organization, such as projecting provider state synced from Stripe. The
// callback receives a detached plan catalog keyed by plan ID and key, and must
// use it instead of Store.Plan or Store.Plans, which would lock again.
func (s *Store) UpdateOrganization(_ context.Context, organizationID string, mutate func(*core.Organization, map[string]core.Plan) error) error {
	if organizationID == "" || mutate == nil {
		return core.ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.organizations[organizationID]
	if !ok {
		return core.ErrNotFound
	}
	plans := make(map[string]core.Plan, 2*len(s.plans))
	for _, plan := range s.plans {
		plans[plan.ID] = plan
		plans[plan.Key] = plan
	}
	next := core.CloneOrganization(current)
	if err := mutate(&next, plans); err != nil {
		return err
	}
	if next.ID != current.ID {
		return core.ErrInvalid
	}
	if !s.providerUniqueLocked(organizationID, next) {
		return core.ErrConflict
	}
	s.organizations[organizationID] = core.CloneOrganization(next)
	return nil
}

func (s *Store) Plans(_ context.Context) ([]core.Plan, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return slices.AppendSeq(make([]core.Plan, 0, len(s.plans)), maps.Values(s.plans)), nil
}

func (s *Store) Plan(_ context.Context, idOrKey string) (core.Plan, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, plan := range s.plans {
		if plan.ID == idOrKey || plan.Key == idOrKey {
			return plan, nil
		}
	}
	return core.Plan{}, core.ErrNotFound
}

func (s *Store) UpsertPlanAs(_ context.Context, p core.Principal, plan core.Plan) error {
	if plan.ID == "" || plan.Key == "" || plan.PriceCents < 0 || plan.AnnualPriceCents < 0 {
		return core.ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	if err := s.validatePlanLocked(plan); err != nil {
		return err
	}
	s.plans[plan.ID] = plan
	return nil
}

func (s *Store) validatePlanLocked(plan core.Plan) error {
	if plan.ID == "" || plan.Key == "" || plan.PriceCents < 0 || plan.AnnualPriceCents < 0 {
		return core.ErrInvalid
	}
	// Keys identify provider prices and pending checkouts. Keep each plan's key
	// stable and avoid ambiguous ID/key lookups across the catalog.
	for existingID, existing := range s.plans {
		if existingID == plan.ID {
			if existing.Key != plan.Key {
				return core.ErrConflict
			}
			continue
		}
		if existing.Key == plan.Key || existingID == plan.Key || existing.Key == plan.ID {
			return core.ErrConflict
		}
	}
	return nil
}

func (s *Store) UpsertPlanAndUpdateOrganizationAs(_ context.Context, p core.Principal, organizationID string, plan core.Plan, mutate func(*core.Organization) error) error {
	if organizationID == "" || mutate == nil {
		return core.ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return core.ErrNotFound
	}
	if err := core.AuthorizeAccount(s.viewLocked(a), p); err != nil {
		return err
	}
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	if err := s.validatePlanLocked(plan); err != nil {
		return err
	}
	current, ok := s.organizations[organizationID]
	if !ok {
		return core.ErrNotFound
	}
	next := core.CloneOrganization(current)
	if err := mutate(&next); err != nil {
		return err
	}
	if next.ID != current.ID {
		return core.ErrInvalid
	}
	if next.Billing.Internal {
		for id, org := range s.organizations {
			if id != organizationID && org.Billing.Internal {
				return core.ErrConflict
			}
		}
	}
	if !s.providerUniqueLocked(organizationID, next) {
		return core.ErrConflict
	}
	s.plans[plan.ID] = plan
	s.organizations[organizationID] = core.CloneOrganization(next)
	return nil
}

func (s *Store) DeletePlanAs(_ context.Context, p core.Principal, planID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	if _, exists := s.plans[planID]; !exists {
		return core.ErrNotFound
	}
	now := time.Now()
	for _, org := range s.organizations {
		if org.PlanID == planID {
			return core.ErrConflict
		}
		for _, session := range org.CheckoutSessions {
			if session.PlanID == planID && session.Status == "open" && (!session.Demo || session.ExpiresAt.After(now)) {
				return core.ErrConflict
			}
		}
	}
	delete(s.plans, planID)
	return nil
}

// AuthorizePlatformAdmin reports whether the principal still matches its
// account and belongs to a platform administrator, without copying state.
func (s *Store) AuthorizePlatformAdmin(_ context.Context, p core.Principal) error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return core.ErrNotFound
	}
	if err := core.AuthorizeAccount(s.viewLocked(a), p); err != nil {
		return err
	}
	return s.requirePlatformAdminLocked(p)
}

func (s *Store) PlatformAccounts(_ context.Context, p core.Principal) ([]core.Account, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return nil, err
	}
	accounts := make([]core.Account, 0, len(s.accounts))
	for _, account := range s.accounts {
		accounts = append(accounts, s.materialize(account))
	}
	return accounts, nil
}

func (s *Store) UpdateAccountAsAdmin(_ context.Context, p core.Principal, targetAccountID string, mutate func(*core.Account) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	return s.updateLocked(targetAccountID, mutate)
}

func (s *Store) requirePlatformAdminLocked(p core.Principal) error {
	a, ok := s.accounts[p.AccountID]
	if !ok || a.User.ID != p.UserID || !a.User.PlatformAdmin {
		return core.ErrForbidden
	}
	return nil
}
