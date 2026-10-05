package memstore

import (
	"slices"

	"terrascale.tech/dashboard/internal/core"
)

// State is a detached domain snapshot, used to run the same validation against
// an aggregate loaded from a persistent store. It is never a storage format.
type State struct {
	Accounts      map[string]core.Account
	Organizations map[string]core.Organization
	Plans         map[string]core.Plan
	AdminActions  []core.AdminAction
}

// FromState creates a request-local validation workspace. Unlike New it does
// not seed defaults: the supplied plan catalog is authoritative.
func FromState(state State) *Store {
	s := New()
	s.plans = make(map[string]core.Plan, len(state.Plans))
	for id, plan := range state.Plans {
		s.plans[id] = plan
	}
	for _, account := range state.Accounts {
		s.putLocked(account)
		s.byEmail[core.NormalizeEmail(account.User.Email)] = account.ID
		if account.User.AuthSubject != "" {
			s.bySubject[account.User.AuthSubject] = account.ID
		}
	}
	for id, org := range state.Organizations {
		s.organizations[id] = core.CloneOrganization(org)
	}
	s.adminActions = slices.Clone(state.AdminActions)
	s.repairSelectionsLocked()
	return s
}

// Export returns isolated account fields and canonical organizations.
func (s *Store) Export() State {
	s.mu.RLock()
	defer s.mu.RUnlock()
	state := State{
		Accounts:      make(map[string]core.Account, len(s.accounts)),
		Organizations: make(map[string]core.Organization, len(s.organizations)),
		Plans:         make(map[string]core.Plan, len(s.plans)),
		AdminActions:  slices.Clone(s.adminActions),
	}
	for id, account := range s.accounts {
		state.Accounts[id] = detachAccount(account)
	}
	for id, org := range s.organizations {
		state.Organizations[id] = core.CloneOrganization(org)
	}
	for id, plan := range s.plans {
		state.Plans[id] = plan
	}
	return state
}
