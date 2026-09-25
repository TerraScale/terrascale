package core

import (
	"slices"
	"strings"
	"time"
)

// RecordPlatformAdminAction appends one platform-wide demo audit record.
// Authorization and actor identity come from the persisted principal, not
// fields supplied by the caller.
func (s *Store) RecordPlatformAdminAction(p Principal, action, target, reason string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return err
	}
	if strings.TrimSpace(action) == "" || strings.TrimSpace(target) == "" || strings.TrimSpace(reason) == "" {
		return ErrInvalid
	}
	s.adminActions = append(s.adminActions, AdminAction{
		ActorUserID: p.UserID,
		Action:      action,
		Target:      target,
		Reason:      reason,
		RecordedAt:  time.Now().UTC(),
	})
	return nil
}

// PlatformAdminActions returns a copy of the platform-wide demo action log.
func (s *Store) PlatformAdminActions(p Principal) ([]AdminAction, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if err := s.requirePlatformAdminLocked(p); err != nil {
		return nil, err
	}
	return slices.Clone(s.adminActions), nil
}
