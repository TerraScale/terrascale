package memstore

import (
	"context"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// RecordPlatformAdminAction appends one platform-wide demo audit record.
// Authorization and actor identity come from the persisted principal, not
// fields supplied by the caller.
func (s *Store) RecordPlatformAdminAction(_ context.Context, p core.Principal, action, target, reason string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	if strings.TrimSpace(action) == "" || strings.TrimSpace(target) == "" || strings.TrimSpace(reason) == "" {
		return core.ErrInvalid
	}
	s.adminActions = append(s.adminActions, core.AdminAction{ID: core.NewID(),
		ActorUserID: p.UserID,
		Action:      action,
		Target:      target,
		Reason:      reason,
		RecordedAt:  time.Now().UTC(),
	})
	return nil
}

// SetPlatformAdmin grants or revokes platform administration for one account
// and records the change, with the account as its actor, in the action log.
func (s *Store) SetPlatformAdmin(_ context.Context, accountID string, admin bool) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	a, ok := s.accounts[accountID]
	if !ok {
		return false, core.ErrNotFound
	}
	if a.User.PlatformAdmin == admin {
		return false, nil
	}
	if err := s.updateLocked(accountID, func(a *core.Account) error {
		a.User.PlatformAdmin = admin
		return nil
	}); err != nil {
		return false, err
	}
	action := "platformAdmin.revoke"
	if admin {
		action = "platformAdmin.grant"
	}
	s.adminActions = append(s.adminActions, core.AdminAction{ID: core.NewID(),
		ActorUserID: a.User.ID,
		Action:      action,
		Target:      a.User.Email,
		Reason:      "dashboard grant-platform-admin",
		RecordedAt:  time.Now().UTC(),
	})
	return true, nil
}

// PlatformAdminActions returns a copy of the platform-wide demo action log.
func (s *Store) PlatformAdminActions(_ context.Context, p core.Principal) ([]core.AdminAction, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return nil, err
	}
	return slices.Clone(s.adminActions), nil
}
