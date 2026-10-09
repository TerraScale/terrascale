package terrabase

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	v1 "terrascale.tech/sdk/v1"
)

type coreWrite struct {
	catalog      bool
	deletePlan   string
	adminActions bool
}

func validateCore(before, after memstore.State, now time.Time) error {
	for _, a := range after.Accounts {
		live := 0
		for _, token := range a.AccessTokens {
			if !token.Expired(now) {
				live++
			}
		}
		if live > core.MaxAccessTokens {
			return core.ErrConflict
		}
	}
	for id, org := range after.Organizations {
		old, exists := before.Organizations[id]
		if exists && reflect.DeepEqual(old, org) {
			continue
		}
		plan, ok := after.Plans[org.PlanID]
		if org.PlanID != "" && !ok {
			return core.ErrNotFound
		}
		if n := org.DatabaseCount(); plan.MaxDatabases > 0 && n > plan.MaxDatabases && n > old.DatabaseCount() {
			return core.ErrDatabaseLimit
		}
		members := func(org core.Organization) int {
			n := 0
			for _, member := range org.Members {
				if member.Status == "" || strings.EqualFold(member.Status, "active") {
					n++
				}
			}
			for _, invitation := range org.Invitations {
				if strings.EqualFold(invitation.Status, "pending") && (invitation.ExpiresAt.IsZero() || now.Before(invitation.ExpiresAt)) {
					n++
				}
			}
			return n
		}
		if n := members(org); plan.MaxMembers > 0 && n > plan.MaxMembers && n > members(old) {
			return core.ErrConflict
		}
		for _, checkout := range org.CheckoutSessions {
			if checkout.Status == "open" && (!checkout.Demo || now.Before(checkout.ExpiresAt)) {
				if _, exists := after.Plans[checkout.PlanID]; !exists {
					return core.ErrNotFound
				}
			}
		}
	}
	return nil
}

// loadClaims reads only natural keys used by the loaded and mutated records.
// An expired pending invitation can release or replace its old email slot.
func (a *coreSnapshot) loadClaims(ctx context.Context, next map[coreKey]coreRecord, now time.Time) error {
	previous, err := encodeCore(a.state, now)
	if err != nil {
		return err
	}
	keys := make(map[coreKey]bool)
	for k, r := range previous {
		if r.Kind == "unique_claim" {
			keys[k] = true
		}
	}
	for k, r := range next {
		if r.Kind == "unique_claim" {
			keys[k] = true
		}
	}
	expired := make(map[coreKey]map[string]bool)
	for _, org := range a.state.Organizations {
		for _, invitation := range org.Invitations {
			if !strings.EqualFold(invitation.Status, "pending") {
				continue
			}
			k := claimKey("invitations", "pending", org.ID, core.NormalizeEmail(invitation.Email))
			keys[k] = true
			if !invitation.ExpiresAt.IsZero() && !now.Before(invitation.ExpiresAt) {
				if expired[k] == nil {
					expired[k] = make(map[string]bool)
				}
				expired[k][invitation.ID] = true
			}
		}
	}
	for k := range keys {
		r, err := a.get(ctx, k)
		if errors.Is(err, core.ErrNotFound) {
			continue
		}
		if err != nil {
			return err
		}
		if r.Kind != "unique_claim" {
			return coreUnavailable(fmt.Errorf("invalid uniqueness claim in %s", k.collection))
		}
		if previous[k].OwnerID != r.OwnerID && !expired[k][r.OwnerID] {
			return core.ErrConflict
		}
	}
	return nil
}

func coreRoot(k coreKey, r coreRecord) coreKey {
	if k.collection == "users" || k.collection == "organizations" {
		return k
	}
	if r.AccountID != "" {
		return recordKey("users", r.AccountID)
	}
	if r.OrgID != "" {
		return recordKey("organizations", r.OrgID)
	}
	return coreKey{}
}

func (s *Store) commitCore(ctx context.Context, a *coreSnapshot, after memstore.State, write coreWrite, now time.Time) error {
	if err := validateCore(a.state, after, now); err != nil {
		return err
	}
	next, err := encodeCore(after, now)
	if err != nil {
		return err
	}
	if err := a.loadClaims(ctx, next, now); err != nil {
		return err
	}
	// Audit retention belongs to the creation event; a later plan/config edit
	// must not silently move the original purge deadline.
	for k, r := range next {
		if k.collection == "audit_events" {
			if old, exists := a.records[k]; exists {
				r.PurgeAt = old.record.PurgeAt
				next[k] = r
			}
		}
	}
	dirty := make(map[coreKey]bool)
	for k, r := range next {
		old, exists := a.records[k]
		// Root revisions change only when the aggregate actually changes.
		r.Rev = old.record.Rev
		next[k] = r
		if !exists || !reflect.DeepEqual(old.record, r) {
			if root := coreRoot(k, r); root.collection != "" {
				dirty[root] = true
			}
		}
	}
	for k, old := range a.records {
		if k.collection == "plans" || k.collection == "admin_actions" {
			continue
		}
		if _, exists := next[k]; !exists {
			if root := coreRoot(k, old.record); root.collection != "" {
				dirty[root] = true
			}
		}
	}
	for k := range dirty {
		if r, exists := next[k]; exists {
			r.Rev = a.records[k].record.Rev + 1
			next[k] = r
		}
	}
	tx := s.db.Tx()
	createOps := make(map[int]bool)
	guarded := make(map[coreKey]bool)
	for k, r := range next {
		old, exists := a.records[k]
		if exists && reflect.DeepEqual(old.record, r) {
			continue
		}
		if k.collection == "plans" {
			r.Rev = old.record.Rev + 1
		}
		if err := checkCoreSize(r); err != nil {
			return err
		}
		if exists {
			tx.Put(k.collection, k.id, r, v1.IfMatch(old.version))
		} else {
			createOps[tx.Len()] = true
			tx.Create(k.collection, k.id, r)
		}
		guarded[k] = true
	}
	for k, old := range a.records {
		if k == recordKey("plans", coreCatalogID) || k.collection == "admin_actions" {
			continue
		}
		if _, exists := next[k]; !exists {
			tx.Delete(k.collection, k.id, v1.IfMatch(old.version))
			guarded[k] = true
		}
	}
	if write.adminActions {
		for _, action := range after.AdminActions[len(a.state.AdminActions):] {
			data, err := encodeData(action)
			if err != nil {
				return core.ErrInvalid
			}
			r := coreRecord{Kind: "record", ID: action.ID, RecordedAt: action.RecordedAt, Data: data}
			createOps[tx.Len()] = true
			tx.Create("admin_actions", action.ID, r)
		}
	}
	if tx.Len() == 0 && !write.catalog {
		return nil
	}
	for k := range a.roots {
		if guarded[k] {
			continue
		}
		old := a.records[k]
		if k == recordKey("plans", coreCatalogID) && write.catalog {
			r := old.record
			r.Rev++
			tx.Put(k.collection, k.id, r, v1.IfMatch(old.version))
		} else {
			tx.Assert(k.collection, k.id, v1.Eq("/rev", old.record.Rev))
		}
	}
	// Binding checks stay explicit even though the catalog guard also prevents
	// any plan edit/deletion from racing the validation workspace.
	bound := make(map[string]bool)
	for _, org := range after.Organizations {
		if _, exists := after.Plans[org.PlanID]; exists {
			bound[org.PlanID] = true
		}
		for _, checkout := range org.CheckoutSessions {
			if checkout.Status == "open" && (!checkout.Demo || now.Before(checkout.ExpiresAt)) {
				bound[checkout.PlanID] = true
			}
		}
	}
	for id := range bound {
		k := recordKey("plans", id)
		if !guarded[k] {
			tx.Assert(k.collection, k.id, v1.Eq("/rev", a.records[k].record.Rev))
		}
	}
	countOps := make(map[int]bool)
	if write.deletePlan != "" {
		zero := uint64(0)
		countOps[tx.Len()] = true
		tx.AssertCount("organizations", v1.Eq("/plan_id", write.deletePlan), v1.CountBounds{Lte: &zero})
		countOps[tx.Len()] = true
		tx.AssertCount("checkout_sessions", v1.And(v1.Eq("/plan_id", write.deletePlan), v1.Eq("/status", "open"), v1.Or(v1.Ne("/demo", true), v1.Gt("/expires_at_ms", now.UnixMilli()))), v1.CountBounds{Lte: &zero})
	}
	if tx.Len() > 1024 {
		return core.Invalid("dashboard mutation exceeds 1024 transaction operations")
	}
	err = commitDocuments(ctx, tx)
	if err == nil {
		return nil
	}
	// A transport failure, uncertain commit, or unclassified backing error
	// must never rerun mutate. There is no server idempotency key in v1.
	if errors.Is(err, v1.ErrCommitUncertain) {
		return coreUnavailable(err)
	}
	if errors.Is(err, v1.ErrPreconditionFailed) {
		var api *v1.APIError
		if errors.As(err, &api) && api.Op != nil && (createOps[*api.Op] || countOps[*api.Op]) {
			return core.ErrConflict
		}
		return errCoreChanged
	}
	if errors.Is(err, v1.ErrorCode("unique_violation")) {
		return core.ErrConflict
	}
	return coreUnavailable(err)
}

func (s *Store) writeCore(ctx context.Context, need coreLoad, write coreWrite, mutate func(*memstore.Store) error) error {
	for range coreAttempts {
		a, err := s.loadCore(ctx, need)
		if errors.Is(err, errCoreChanged) {
			continue
		}
		if err != nil {
			return err
		}
		workspace := memstore.FromState(a.state)
		if err := mutate(workspace); err != nil {
			return err
		}
		err = s.commitCore(ctx, a, workspace.Export(), write, time.Now().UTC())
		if errors.Is(err, errCoreChanged) {
			continue
		}
		return err
	}
	return coreUnavailable(errCoreChanged)
}
