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
	s.adminActions = append(s.adminActions, core.AdminAction{
		ActorUserID: p.UserID,
		Action:      action,
		Target:      target,
		Reason:      reason,
		RecordedAt:  time.Now().UTC(),
	})
	return nil
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
