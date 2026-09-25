package core

import (
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
			if org.Members == nil {
				org.Members = make(map[string]Member)
			}
			org.Members[a.User.ID] = Member{
				UserID: a.User.ID, Email: a.User.Email, Name: a.User.Name,
				Role: invitation.Role, Status: "active", CreatedAt: time.Now().UTC(),
			}
			invitation.Status = "accepted"
			org.Invitations[invitationID] = invitation
			next := s.materialize(a)
			next.Organizations[id] = cloneOrganization(org)
			next.ActiveOrganizationID = id
			next.ActiveProjectID = ""
			s.organizations[id] = org
			s.accounts[a.ID] = cloneAccount(next)
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

// ApplyBillingEvent finds the canonical tenant by provider identity. It marks
// an event processed only after the projection callback succeeds.
func (s *Store) ApplyBillingEvent(eventID, customerID, subscriptionID string, mutate func(*Organization) error) error {
	if mutate == nil {
		return ErrInvalid
	}
	return s.applyBillingEvent(eventID, customerID, subscriptionID, mutate)
}

// ApplyBillingEventWithPlans supplies a detached plan catalog while holding
// the event commit lock. Projection callbacks must use this snapshot instead
// of calling Store.Plan or Store.Plans, which would acquire the lock again.
func (s *Store) ApplyBillingEventWithPlans(eventID, customerID, subscriptionID string, mutate func(*Organization, map[string]Plan) error) error {
	if mutate == nil {
		return ErrInvalid
	}
	return s.applyBillingEvent(eventID, customerID, subscriptionID, func(org *Organization) error {
		plans := make(map[string]Plan, 2*len(s.plans))
		for _, plan := range s.plans {
			plans[plan.ID] = plan
			plans[plan.Key] = plan
		}
		return mutate(org, plans)
	})
}

func (s *Store) applyBillingEvent(eventID, customerID, subscriptionID string, mutate func(*Organization) error) error {
	if eventID == "" || (customerID == "" && subscriptionID == "") || mutate == nil {
		return ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, exists := s.billingEvents[eventID]; exists {
		return ErrAlreadyProcessed
	}
	var matchedID string
	var matched Organization
	for id, org := range s.organizations {
		if (customerID == "" || org.Billing.CustomerID != customerID) &&
			(subscriptionID == "" || org.Billing.SubscriptionID != subscriptionID) {
			continue
		}
		if matchedID != "" && matchedID != id {
			return ErrConflict
		}
		matchedID, matched = id, org
	}
	if matchedID == "" {
		return ErrNotFound
	}
	next := cloneOrganization(matched)
	if err := mutate(&next); err != nil {
		return err
	}
	if next.ID != matched.ID {
		return ErrInvalid
	}
	if !s.providerUniqueLocked(matchedID, next) {
		return ErrConflict
	}
	s.organizations[matchedID] = cloneOrganization(next)
	s.billingEvents[eventID] = struct{}{}
	return nil
}

func (s *Store) Plans() []Plan {
	s.mu.RLock()
	defer s.mu.RUnlock()
	plans := make([]Plan, 0, len(s.plans))
	for _, plan := range s.plans {
		plans = append(plans, plan)
	}
	return plans
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
