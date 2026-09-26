package core

import (
	"maps"
	"slices"
	"strings"
	"time"
)

// FindInvitation returns a copy of a pending invitation without revealing the
// stored token hash or granting access to its organization.
func (s *Store) FindInvitation(tokenHash string) (Organization, Invitation, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if tokenHash == "" {
		return Organization{}, Invitation{}, false
	}
	for _, org := range s.organizations {
		for _, invitation := range org.Invitations {
			if invitation.TokenHash == tokenHash && strings.EqualFold(invitation.Status, "pending") &&
				(invitation.ExpiresAt.IsZero() || time.Now().Before(invitation.ExpiresAt)) {
				invitation.TokenHash = ""
				return Organization{ID: org.ID, Name: org.Name, Kind: org.Kind}, invitation, true
			}
		}
	}
	return Organization{}, Invitation{}, false
}

// AcceptInvitation joins one account to an existing canonical organization.
// The invite, email, membership, and selected scope change in one lock/commit.
func (s *Store) AcceptInvitation(p Principal, tokenHash string) (Organization, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	a, ok := s.accounts[p.AccountID]
	if !ok || a.User.ID != p.UserID || tokenHash == "" {
		return Organization{}, ErrForbidden
	}
	for id, canonical := range s.organizations {
		for invitationID, invitation := range canonical.Invitations {
			if invitation.TokenHash != tokenHash {
				continue
			}
			if !strings.EqualFold(invitation.Status, "pending") ||
				(!invitation.ExpiresAt.IsZero() && !time.Now().Before(invitation.ExpiresAt)) {
				return Organization{}, ErrConflict
			}
			if NormalizeEmail(invitation.Email) != a.User.Email {
				return Organization{}, ErrForbidden
			}
			org := cloneOrganization(canonical)
			if plan, ok := s.plans[org.PlanID]; ok && plan.MaxMembers > 0 {
				active := 0
				for _, member := range org.Members {
					if member.Status == "active" {
						active++
					}
				}
				if active >= plan.MaxMembers {
					return Organization{}, ErrConflict
				}
			}
			if org.Members == nil {
				org.Members = make(map[string]Member)
			}
			org.Members[a.User.ID] = Member{
				UserID: a.User.ID, Email: a.User.Email, Name: a.User.Name,
				Role: invitation.Role, Status: "active", CreatedAt: time.Now().UTC(),
			}
			invitation.Status = "accepted"
			org.Invitations[invitationID] = invitation
			next := detachAccount(s.viewLocked(a))
			next.Organizations[id] = Organization{ID: id}
			next.ActiveOrganizationID = id
			next.ActiveProjectID = ""
			s.organizations[id] = org
			s.accounts[a.ID] = next
			return cloneOrganization(org), nil
		}
	}
	return Organization{}, ErrNotFound
}

func (s *Store) DeclineInvitation(p Principal, tokenHash string) (Organization, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	a, ok := s.accounts[p.AccountID]
	if !ok || a.User.ID != p.UserID || tokenHash == "" {
		return Organization{}, ErrForbidden
	}
	for id, canonical := range s.organizations {
		for invitationID, invitation := range canonical.Invitations {
			if invitation.TokenHash != tokenHash {
				continue
			}
			if !strings.EqualFold(invitation.Status, "pending") ||
				(!invitation.ExpiresAt.IsZero() && !time.Now().Before(invitation.ExpiresAt)) {
				return Organization{}, ErrConflict
			}
			if NormalizeEmail(invitation.Email) != a.User.Email {
				return Organization{}, ErrForbidden
			}
			org := cloneOrganization(canonical)
			invitation.Status = "declined"
			org.Invitations[invitationID] = invitation
			s.organizations[id] = org
			return cloneOrganization(org), nil
		}
	}
	return Organization{}, ErrNotFound
}

// Organization returns a copy of one organization for trusted server-side
// integrations that act without a signed-in principal, such as Stripe syncs.
func (s *Store) Organization(organizationID string) (Organization, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	org, ok := s.organizations[organizationID]
	if !ok {
		return Organization{}, false
	}
	return cloneOrganization(org), true
}

// UpdateOrganization applies a trusted server-side change to one
// organization, such as projecting provider state synced from Stripe. The
// callback receives a detached plan catalog keyed by plan ID and key, and must
// use it instead of Store.Plan or Store.Plans, which would lock again.
func (s *Store) UpdateOrganization(organizationID string, mutate func(*Organization, map[string]Plan) error) error {
	if organizationID == "" || mutate == nil {
		return ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.organizations[organizationID]
	if !ok {
		return ErrNotFound
	}
	plans := make(map[string]Plan, 2*len(s.plans))
	for _, plan := range s.plans {
		plans[plan.ID] = plan
		plans[plan.Key] = plan
	}
	next := cloneOrganization(current)
	if err := mutate(&next, plans); err != nil {
		return err
	}
	if next.ID != current.ID {
		return ErrInvalid
	}
	if !s.providerUniqueLocked(organizationID, next) {
		return ErrConflict
	}
	s.organizations[organizationID] = cloneOrganization(next)
	return nil
}

func (s *Store) Plans() []Plan {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return slices.AppendSeq(make([]Plan, 0, len(s.plans)), maps.Values(s.plans))
}

func (s *Store) Plan(idOrKey string) (Plan, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, plan := range s.plans {
		if plan.ID == idOrKey || plan.Key == idOrKey {
			return plan, true
		}
	}
	return Plan{}, false
}

func (s *Store) UpsertPlanAs(p Principal, plan Plan) error {
	if plan.ID == "" || plan.Key == "" || plan.PriceCents < 0 || plan.AnnualPriceCents < 0 {
		return ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	// Keys identify provider prices and pending checkouts. Keep each plan's key
	// stable and avoid ambiguous ID/key lookups across the catalog.
	for existingID, existing := range s.plans {
		if existingID == plan.ID {
			if existing.Key != plan.Key {
				return ErrConflict
			}
			continue
		}
		if existing.Key == plan.Key || existingID == plan.Key || existing.Key == plan.ID {
			return ErrConflict
		}
	}
	s.plans[plan.ID] = plan
	return nil
}

func (s *Store) DeletePlanAs(p Principal, planID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	if _, exists := s.plans[planID]; !exists {
		return ErrNotFound
	}
	now := time.Now()
	for _, org := range s.organizations {
		if org.PlanID == planID {
			return ErrConflict
		}
		for _, session := range org.CheckoutSessions {
			if session.PlanID == planID && session.Status == "open" && (!session.Demo || session.ExpiresAt.After(now)) {
				return ErrConflict
			}
		}
	}
	delete(s.plans, planID)
	return nil
}

// AuthorizePlatformAdmin reports whether the principal still matches its
// account and belongs to a platform administrator, without copying state.
func (s *Store) AuthorizePlatformAdmin(p Principal) error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return ErrNotFound
	}
	if err := authorize(s.viewLocked(a), p); err != nil {
		return err
	}
	return s.requirePlatformAdminLocked(p)
}

func (s *Store) PlatformAccounts(p Principal) ([]Account, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return nil, err
	}
	accounts := make([]Account, 0, len(s.accounts))
	for _, account := range s.accounts {
		accounts = append(accounts, s.materialize(account))
	}
	return accounts, nil
}

func (s *Store) UpdateAccountAsAdmin(p Principal, targetAccountID string, mutate func(*Account) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	return s.updateLocked(targetAccountID, mutate)
}

func (s *Store) requirePlatformAdminLocked(p Principal) error {
	a, ok := s.accounts[p.AccountID]
	if !ok || a.User.ID != p.UserID || !a.User.PlatformAdmin {
		return ErrForbidden
	}
	return nil
}
