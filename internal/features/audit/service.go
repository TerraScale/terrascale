package audit

import (
	"context"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// Service reads the audit history of a principal's selected organization and
// project. HTTP handlers and MCP tools both call it.
type Service struct {
	store core.Store
}

// Page returns one filtered page of audit events with size rows per page.
func (s Service) Page(ctx context.Context, p core.Principal, search, action, severity, rawRange, rawPage string, size int) (pageData, error) {
	now := time.Now().UTC()
	events, retention, err := s.history(ctx, p, now)
	if err != nil {
		return pageData{}, err
	}
	return buildPage(events, retention, search, action, severity, rawRange, rawPage, size, now), nil
}

// Export returns every retained audit event that matches f, newest first.
func (s Service) Export(ctx context.Context, p core.Principal, f filters, now time.Time) ([]core.AuditEvent, error) {
	events, retention, err := s.history(ctx, p, now)
	if err != nil {
		return nil, err
	}
	return filter(events, f, now, retention), nil
}

// history returns the scope's retained events and its effective retention
// in days.
func (s Service) history(ctx context.Context, p core.Principal, now time.Time) ([]core.AuditEvent, int, error) {
	account, err := s.store.SnapshotAs(ctx, p)
	if err != nil {
		return nil, 0, err
	}
	retention := account.Organizations[p.OrganizationID].AuditRetentionDays
	if retention < 1 || retention > 90 {
		retention = 90
	}
	return retained(scoped(account.AuditEvents, p), retention, now), retention, nil
}
