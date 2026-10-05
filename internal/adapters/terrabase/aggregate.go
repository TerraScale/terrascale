package terrabase

import (
	"context"
	"errors"
	"fmt"
	"sort"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	v1 "terrascale.tech/sdk/v1"
)

const coreAttempts = 5
const coreCatalogID = "__catalog"

var errCoreChanged = errors.New("aggregate changed while loading")

type coreLoaded struct {
	record  coreRecord
	version string
}

type coreSnapshot struct {
	db      *v1.Database
	state   memstore.State
	records map[coreKey]coreLoaded
	roots   map[coreKey]bool
}

type coreLoad struct {
	accounts         []string
	organizations    []string
	email            string
	subject          string
	invitationHash   string
	tokenHash        string
	allAccounts      bool
	allOrganizations bool
	actions          bool
}

func coreUnavailable(err error) error {
	return documentError(err)
}

func (a *coreSnapshot) remember(k coreKey, d *v1.Document) (coreRecord, error) {
	if err := validateDocument(d); err != nil {
		return coreRecord{}, err
	}
	var r coreRecord
	if err := d.Decode(&r); err != nil {
		return r, coreUnavailable(err)
	}
	if old, exists := a.records[k]; exists && old.version != d.Version {
		return r, errCoreChanged
	}
	a.records[k] = coreLoaded{r, d.Version}
	return r, nil
}

func (a *coreSnapshot) get(ctx context.Context, k coreKey) (coreRecord, error) {
	if r, ok := a.records[k]; ok {
		return r.record, nil
	}
	d, err := a.db.Collection(k.collection).Get(ctx, k.id)
	if errors.Is(err, v1.ErrNotFound) {
		return coreRecord{}, core.ErrNotFound
	}
	if err != nil {
		return coreRecord{}, coreUnavailable(err)
	}
	return a.remember(k, d)
}

func (a *coreSnapshot) query(ctx context.Context, collection string, filter v1.Filter, visit func(coreRecord) error) error {
	if filter.IsZero() {
		filter = v1.Eq("/kind", "record")
	} else {
		filter = v1.And(v1.Eq("/kind", "record"), filter)
	}
	return queryDocuments(ctx, a.db, collection, v1.Query{Filter: filter, Limit: 1000}, func(d v1.Document) error {
		r, err := a.remember(coreKey{collection, d.ID}, &d)
		if err != nil {
			return err
		}
		if err := visit(r); err != nil {
			return err
		}
		return nil
	})
}

func (a *coreSnapshot) organization(ctx context.Context, id string) error {
	if _, exists := a.state.Organizations[id]; exists {
		return nil
	}
	k := recordKey("organizations", id)
	r, err := a.get(ctx, k)
	if err != nil {
		return err
	}
	if r.Kind != "record" || r.ID != id {
		return coreUnavailable(fmt.Errorf("invalid organization root %s", id))
	}
	org, err := decodeData[core.Organization](r)
	if err != nil {
		return err
	}
	org = core.CloneOrganization(org)
	a.roots[k] = true
	billing, err := a.get(ctx, recordKey("billing", id))
	if err != nil {
		return err
	}
	org.Billing, err = decodeData[core.Billing](billing)
	if err != nil {
		return err
	}
	filter := v1.Eq("/org_id", id)
	var databaseRecords []coreRecord
	loads := []struct {
		collection string
		visit      func(coreRecord) error
	}{
		{"memberships", func(r coreRecord) error {
			member, err := decodeData[core.Member](r)
			org.Members[member.UserID] = member
			return err
		}},
		{"invitations", func(r coreRecord) error {
			invitation, err := decodeData[core.Invitation](r)
			org.Invitations[invitation.ID] = invitation
			return err
		}},
		{"checkout_sessions", func(r coreRecord) error {
			checkout, err := decodeData[core.CheckoutSession](r)
			org.CheckoutSessions[checkout.ID] = checkout
			return err
		}},
		{"projects", func(r coreRecord) error {
			project, err := decodeData[core.Project](r)
			if err != nil {
				return err
			}
			project.Databases = make(map[string]core.Database)
			project.APIKeys = make(map[string]core.APIKey)
			org.Projects[project.ID] = project
			return nil
		}},
		{"databases", func(r coreRecord) error {
			databaseRecords = append(databaseRecords, r)
			return nil
		}},
		{"api_keys", func(r coreRecord) error {
			key, err := decodeData[core.APIKey](r)
			if err != nil {
				return err
			}
			project, exists := org.Projects[key.ProjectID]
			if !exists {
				return errCoreChanged
			}
			project.APIKeys[key.ID] = key
			return nil
		}},
	}
	for _, load := range loads {
		if err := a.query(ctx, load.collection, filter, load.visit); err != nil {
			return err
		}
	}
	if err := materializeDatabases(&org, databaseRecords); err != nil {
		return err
	}
	a.state.Organizations[id] = org
	return nil
}

func (a *coreSnapshot) account(ctx context.Context, id string) error {
	if _, exists := a.state.Accounts[id]; exists {
		return nil
	}
	k := recordKey("users", id)
	r, err := a.get(ctx, k)
	if err != nil {
		return err
	}
	if r.Kind != "record" || r.ID != id {
		return coreUnavailable(fmt.Errorf("invalid account root %s", id))
	}
	account, err := decodeData[core.Account](r)
	if err != nil {
		return err
	}
	account.Organizations = make(map[string]core.Organization)
	a.roots[k] = true
	if err := a.query(ctx, "memberships", v1.And(v1.Eq("/user_id", account.User.ID), v1.Eq("/status", "active")), func(r coreRecord) error {
		if err := a.organization(ctx, r.OrgID); err != nil {
			if errors.Is(err, core.ErrNotFound) {
				return errCoreChanged
			}
			return err
		}
		account.Organizations[r.OrgID] = a.state.Organizations[r.OrgID]
		return nil
	}); err != nil {
		return err
	}
	filter := v1.Eq("/user_id", account.User.ID)
	for _, load := range []struct {
		collection string
		visit      func(coreRecord) error
	}{
		{"access_tokens", func(r coreRecord) error {
			token, err := decodeData[core.AccessToken](r)
			if r.AuthVersion == account.AuthVersion {
				account.AccessTokens = append(account.AccessTokens, token)
			}
			return err
		}},
		{"notifications", func(r coreRecord) error {
			n, err := decodeData[core.Notification](r)
			account.Notifications = append(account.Notifications, n)
			return err
		}},
		{"audit_events", func(r coreRecord) error {
			e, err := decodeData[core.AuditEvent](r)
			account.AuditEvents = append(account.AuditEvents, e)
			return err
		}},
	} {
		if err := a.query(ctx, load.collection, filter, load.visit); err != nil {
			return err
		}
	}
	sort.Slice(account.AccessTokens, func(i, j int) bool {
		return account.AccessTokens[i].CreatedAt.Before(account.AccessTokens[j].CreatedAt)
	})
	sort.Slice(account.Notifications, func(i, j int) bool {
		return account.Notifications[i].CreatedAt.Before(account.Notifications[j].CreatedAt)
	})
	sort.Slice(account.AuditEvents, func(i, j int) bool {
		return account.AuditEvents[i].OccurredAt.Before(account.AuditEvents[j].OccurredAt)
	})
	core.RepairSelections(&account)
	a.state.Accounts[id] = account
	return nil
}

func (s *Store) loadCore(ctx context.Context, need coreLoad) (*coreSnapshot, error) {
	if s.db == nil {
		return nil, coreUnavailable(errors.New("dashboard database is not configured"))
	}
	a := &coreSnapshot{
		db: s.db, records: make(map[coreKey]coreLoaded), roots: make(map[coreKey]bool),
		state: memstore.State{Accounts: make(map[string]core.Account), Organizations: make(map[string]core.Organization), Plans: make(map[string]core.Plan)},
	}
	k := recordKey("plans", coreCatalogID)
	if _, err := a.get(ctx, k); err != nil {
		return nil, coreUnavailable(fmt.Errorf("plan catalog is not initialized: %w", err))
	}
	a.roots[k] = true
	if err := a.query(ctx, "plans", v1.Filter{}, func(r coreRecord) error {
		plan, err := decodeData[core.Plan](r)
		a.state.Plans[plan.ID] = plan
		return err
	}); err != nil {
		return nil, err
	}
	if need.email != "" {
		r, err := a.get(ctx, claimKey("users", "email", core.NormalizeEmail(need.email)))
		if err != nil {
			return nil, err
		}
		need.accounts = append(need.accounts, r.OwnerID)
	}
	if need.subject != "" {
		r, err := a.get(ctx, claimKey("users", "auth_subject", need.subject))
		if err != nil {
			return nil, err
		}
		need.accounts = append(need.accounts, r.OwnerID)
	}
	if need.invitationHash != "" {
		r, err := a.get(ctx, claimKey("invitations", "token_hash", need.invitationHash))
		if err != nil {
			return nil, err
		}
		invitation, err := a.get(ctx, recordKey("invitations", r.OwnerID))
		if err != nil {
			return nil, err
		}
		need.organizations = append(need.organizations, invitation.OrgID)
	}
	if need.tokenHash != "" {
		r, err := a.get(ctx, recordKey("access_tokens", need.tokenHash))
		if err != nil {
			return nil, err
		}
		need.accounts = append(need.accounts, r.AccountID)
	}
	if need.allAccounts {
		if err := a.query(ctx, "users", v1.Filter{}, func(r coreRecord) error {
			need.accounts = append(need.accounts, r.ID)
			return nil
		}); err != nil {
			return nil, err
		}
	}
	if need.allOrganizations {
		if err := a.query(ctx, "organizations", v1.Filter{}, func(r coreRecord) error {
			need.organizations = append(need.organizations, r.ID)
			return nil
		}); err != nil {
			return nil, err
		}
	}
	for _, id := range need.accounts {
		if err := a.account(ctx, id); err != nil {
			return nil, err
		}
	}
	for _, id := range need.organizations {
		if err := a.organization(ctx, id); err != nil {
			return nil, err
		}
	}
	if need.actions {
		if err := a.query(ctx, "admin_actions", v1.Filter{}, func(r coreRecord) error {
			action, err := decodeData[core.AdminAction](r)
			a.state.AdminActions = append(a.state.AdminActions, action)
			return err
		}); err != nil {
			return nil, err
		}
		sort.Slice(a.state.AdminActions, func(i, j int) bool {
			return a.state.AdminActions[i].RecordedAt.Before(a.state.AdminActions[j].RecordedAt)
		})
	}
	for k := range a.roots {
		_, err := s.db.Collection(k.collection).Get(ctx, k.id, v1.IfNoneMatch(a.records[k].version))
		if errors.Is(err, v1.ErrNotModified) {
			continue
		}
		if err == nil || errors.Is(err, v1.ErrNotFound) {
			return nil, errCoreChanged
		}
		return nil, coreUnavailable(err)
	}
	return a, nil
}

func (s *Store) readCore(ctx context.Context, need coreLoad, read func(*memstore.Store) error) error {
	for range coreAttempts {
		a, err := s.loadCore(ctx, need)
		if errors.Is(err, errCoreChanged) {
			continue
		}
		if err != nil {
			return err
		}
		return read(memstore.FromState(a.state))
	}
	return coreUnavailable(errCoreChanged)
}
