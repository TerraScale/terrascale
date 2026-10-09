package terrabase

import (
	"cmp"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	v1 "terrascale.tech/sdk/v1"
)

// Store persists dashboard domain state in one dedicated Documents database.
// No aggregate or validation workspace survives a method call.
type Store struct{ db *v1.Database }

var _ core.Store = (*Store)(nil)

// New constructs a handle without I/O. Provision collections and SeedPlans
// explicitly before using it; startup and readiness checks never provision.
func New(db *v1.Database) *Store { return &Store{db: db} }

// SeedPlans atomically initializes a new database's default catalog. An
// existing catalog is authoritative, including intentional plan deletions.
func (s *Store) SeedPlans(ctx context.Context) error {
	if s.db == nil {
		return coreUnavailable(errors.New("dashboard database is not configured"))
	}
	k := recordKey("plans", coreCatalogID)
	if _, err := s.db.Collection(k.collection).Get(ctx, k.id); err == nil {
		return nil
	} else if !errors.Is(err, v1.ErrNotFound) {
		return coreUnavailable(err)
	}
	tx := s.db.Tx().Create(k.collection, k.id, coreRecord{Kind: "catalog_lock", ID: coreCatalogID, Rev: 1})
	for _, plan := range core.DefaultPlans() {
		data, err := json.Marshal(plan)
		if err != nil {
			return coreUnavailable(err)
		}
		tx.Create("plans", v1.DocumentIDFor("plans", plan.ID), coreRecord{Kind: "record", ID: plan.ID, Key: plan.Key, MaxDatabases: plan.MaxDatabases, MaxMembers: plan.MaxMembers, Rev: 1, Data: data})
	}
	_, err := tx.Commit(ctx)
	if err == nil {
		return nil
	}
	if errors.Is(err, v1.ErrPreconditionFailed) {
		if _, readErr := s.db.Collection(k.collection).Get(ctx, k.id); readErr == nil {
			return nil
		}
	}
	return coreUnavailable(err)
}

func (s *Store) InsertAccount(ctx context.Context, a core.Account) error {
	a = core.CloneAccount(a)
	return s.writeCore(ctx, coreLoad{}, coreWrite{}, func(m *memstore.Store) error { return m.InsertAccount(ctx, a) })
}

func (s *Store) CreateAccount(ctx context.Context, user core.User) (core.Account, error) {
	a, err := core.NewAccount(user)
	if err != nil {
		return core.Account{}, err
	}
	if err := s.InsertAccount(ctx, a); err != nil {
		return core.Account{}, err
	}
	return core.CloneAccount(a), nil
}

func (s *Store) FindByEmail(ctx context.Context, email string) (out core.Account, err error) {
	email = core.NormalizeEmail(email)
	if email == "" {
		return out, core.ErrNotFound
	}
	err = s.readCore(ctx, coreLoad{email: email}, func(m *memstore.Store) error {
		out, err = m.FindByEmail(ctx, email)
		return err
	})
	return
}

func (s *Store) FindBySubject(ctx context.Context, subject string) (out core.Account, err error) {
	if subject == "" {
		return out, core.ErrNotFound
	}
	err = s.readCore(ctx, coreLoad{subject: subject}, func(m *memstore.Store) error {
		out, err = m.FindBySubject(ctx, subject)
		return err
	})
	return
}

func (s *Store) SnapshotAs(ctx context.Context, p core.Principal) (out core.Account, err error) {
	err = s.readCore(ctx, coreLoad{accounts: []string{p.AccountID}}, func(m *memstore.Store) error {
		out, err = m.SnapshotAs(ctx, p)
		return err
	})
	return
}

func (s *Store) ViewAs(ctx context.Context, p core.Principal, read func(core.Account) error) error {
	return s.readCore(ctx, coreLoad{accounts: []string{p.AccountID}}, func(m *memstore.Store) error { return m.ViewAs(ctx, p, read) })
}

func (s *Store) ScopedPrincipal(ctx context.Context, p core.Principal, scope core.Scope) (out core.Principal, err error) {
	var scopeErr error
	err = s.readCore(ctx, coreLoad{accounts: []string{p.AccountID}}, func(m *memstore.Store) error {
		out, scopeErr = m.ScopedPrincipal(ctx, p, scope)
		return nil
	})
	// A missing account is a principal that no longer authenticates; a
	// scope outside the account stays ErrNotFound, as in memstore.
	if errors.Is(err, core.ErrNotFound) && !errors.Is(err, core.ErrUnavailable) {
		err = core.ErrUnauthorized
	}
	return out, cmp.Or(err, scopeErr)
}

func (s *Store) CurrentPrincipal(ctx context.Context, accountID, userID string) (out core.Principal, err error) {
	err = s.readCore(ctx, coreLoad{accounts: []string{accountID}}, func(m *memstore.Store) error {
		out, err = m.CurrentPrincipal(ctx, accountID, userID)
		return err
	})
	return
}

func (s *Store) SessionPrincipal(ctx context.Context, accountID, userID string, authVersion uint64) (out core.Principal, err error) {
	err = s.readCore(ctx, coreLoad{accounts: []string{accountID}}, func(m *memstore.Store) error {
		out, err = m.SessionPrincipal(ctx, accountID, userID, authVersion)
		return err
	})
	if errors.Is(err, core.ErrNotFound) && !errors.Is(err, core.ErrUnavailable) {
		err = core.ErrUnauthorized
	}
	return
}

func (s *Store) Update(ctx context.Context, accountID string, mutate func(*core.Account) error) error {
	return s.writeCore(ctx, coreLoad{accounts: []string{accountID}}, coreWrite{}, func(m *memstore.Store) error { return m.Update(ctx, accountID, mutate) })
}

func (s *Store) UpdateAs(ctx context.Context, p core.Principal, mutate func(*core.Account) error) error {
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}}, coreWrite{}, func(m *memstore.Store) error { return m.UpdateAs(ctx, p, mutate) })
}

func (s *Store) UpdateAsWithPlan(ctx context.Context, p core.Principal, planID string, mutate func(*core.Account, core.Plan) error) error {
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}}, coreWrite{}, func(m *memstore.Store) error { return m.UpdateAsWithPlan(ctx, p, planID, mutate) })
}

func (s *Store) ChangeEmailAs(ctx context.Context, p core.Principal, email, expectedPasswordHash string) error {
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}}, coreWrite{}, func(m *memstore.Store) error { return m.ChangeEmailAs(ctx, p, email, expectedPasswordHash) })
}

func (s *Store) FindInvitation(ctx context.Context, tokenHash string) (org core.Organization, invitation core.Invitation, err error) {
	if tokenHash == "" {
		return org, invitation, core.ErrNotFound
	}
	err = s.readCore(ctx, coreLoad{invitationHash: tokenHash}, func(m *memstore.Store) error {
		org, invitation, err = m.FindInvitation(ctx, tokenHash)
		return err
	})
	return
}

func (s *Store) AcceptInvitation(ctx context.Context, p core.Principal, tokenHash string) (out core.Organization, err error) {
	if tokenHash == "" {
		return out, core.ErrForbidden
	}
	err = s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}, invitationHash: tokenHash}, coreWrite{}, func(m *memstore.Store) error {
		out, err = m.AcceptInvitation(ctx, p, tokenHash)
		return err
	})
	return
}

func (s *Store) DeclineInvitation(ctx context.Context, p core.Principal, tokenHash string) (out core.Organization, err error) {
	if tokenHash == "" {
		return out, core.ErrForbidden
	}
	err = s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}, invitationHash: tokenHash}, coreWrite{}, func(m *memstore.Store) error {
		out, err = m.DeclineInvitation(ctx, p, tokenHash)
		return err
	})
	return
}

func (s *Store) Organization(ctx context.Context, organizationID string) (out core.Organization, err error) {
	err = s.readCore(ctx, coreLoad{organizations: []string{organizationID}}, func(m *memstore.Store) error {
		out, err = m.Organization(ctx, organizationID)
		return err
	})
	return
}

func (s *Store) UpdateOrganization(ctx context.Context, organizationID string, mutate func(*core.Organization, map[string]core.Plan) error) error {
	if organizationID == "" || mutate == nil {
		return core.ErrInvalid
	}
	return s.writeCore(ctx, coreLoad{organizations: []string{organizationID}, allOrganizations: true}, coreWrite{}, func(m *memstore.Store) error { return m.UpdateOrganization(ctx, organizationID, mutate) })
}

// Plans and Plan return seeded plans upgraded (core.UpgradeSeededPlan)
// without writing them, so no other commit rewrites the catalog.
func (s *Store) Plans(ctx context.Context) (out []core.Plan, err error) {
	err = s.readCore(ctx, coreLoad{}, func(m *memstore.Store) error { out, err = m.Plans(ctx); return err })
	for i := range out {
		out[i] = core.UpgradeSeededPlan(out[i])
	}
	return
}

func (s *Store) Plan(ctx context.Context, idOrKey string) (out core.Plan, err error) {
	err = s.readCore(ctx, coreLoad{}, func(m *memstore.Store) error { out, err = m.Plan(ctx, idOrKey); return err })
	return core.UpgradeSeededPlan(out), err
}

func (s *Store) UpsertPlanAs(ctx context.Context, p core.Principal, plan core.Plan) error {
	if plan.ID == coreCatalogID || plan.Key == coreCatalogID {
		return core.ErrInvalid
	}
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}}, coreWrite{catalog: true}, func(m *memstore.Store) error { return m.UpsertPlanAs(ctx, p, plan) })
}

func (s *Store) UpsertPlanAndUpdateOrganizationAs(ctx context.Context, p core.Principal, organizationID string, plan core.Plan, mutate func(*core.Organization) error) error {
	if organizationID == "" || mutate == nil || plan.ID == coreCatalogID || plan.Key == coreCatalogID {
		return core.ErrInvalid
	}
	// Guard every organization and the catalog so singleton checks rerun after competing commits.
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}, organizations: []string{organizationID}, allOrganizations: true}, coreWrite{catalog: true}, func(m *memstore.Store) error {
		return m.UpsertPlanAndUpdateOrganizationAs(ctx, p, organizationID, plan, mutate)
	})
}

func (s *Store) DeletePlanAs(ctx context.Context, p core.Principal, planID string) error {
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}, allOrganizations: true}, coreWrite{catalog: true, deletePlan: planID}, func(m *memstore.Store) error { return m.DeletePlanAs(ctx, p, planID) })
}

func (s *Store) AuthorizePlatformAdmin(ctx context.Context, p core.Principal) error {
	return s.readCore(ctx, coreLoad{accounts: []string{p.AccountID}}, func(m *memstore.Store) error { return m.AuthorizePlatformAdmin(ctx, p) })
}

func (s *Store) PlatformAccounts(ctx context.Context, p core.Principal) (out []core.Account, err error) {
	if err = s.AuthorizePlatformAdmin(ctx, p); err != nil {
		return
	}
	// ponytail: core.Store returns the full list; page its interface before
	// supporting an admin catalog too large to materialize per request.
	err = s.readCore(ctx, coreLoad{accounts: []string{p.AccountID}, allAccounts: true}, func(m *memstore.Store) error { out, err = m.PlatformAccounts(ctx, p); return err })
	return
}

func (s *Store) UpdateAccountAsAdmin(ctx context.Context, p core.Principal, targetAccountID string, mutate func(*core.Account) error) error {
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID, targetAccountID}}, coreWrite{}, func(m *memstore.Store) error { return m.UpdateAccountAsAdmin(ctx, p, targetAccountID, mutate) })
}

func newCoreUUIDv7() string {
	var b [16]byte
	rand.Read(b[:])
	millis := uint64(time.Now().UnixMilli())
	for i := 5; i >= 0; i-- {
		b[i] = byte(millis)
		millis >>= 8
	}
	b[6], b[8] = b[6]&15|0x70, b[8]&63|0x80
	h := hex.EncodeToString(b[:])
	return h[:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:]
}

func (s *Store) RecordPlatformAdminAction(ctx context.Context, p core.Principal, action, target, reason string) error {
	return s.writeCore(ctx, coreLoad{accounts: []string{p.AccountID}}, coreWrite{adminID: newCoreUUIDv7()}, func(m *memstore.Store) error { return m.RecordPlatformAdminAction(ctx, p, action, target, reason) })
}

func (s *Store) SetPlatformAdmin(ctx context.Context, accountID string, admin bool) (changed bool, err error) {
	err = s.writeCore(ctx, coreLoad{accounts: []string{accountID}}, coreWrite{adminID: newCoreUUIDv7()}, func(m *memstore.Store) error { changed, err = m.SetPlatformAdmin(ctx, accountID, admin); return err })
	return changed, err
}

func (s *Store) PlatformAdminActions(ctx context.Context, p core.Principal) (out []core.AdminAction, err error) {
	if err = s.AuthorizePlatformAdmin(ctx, p); err != nil {
		return
	}
	err = s.readCore(ctx, coreLoad{accounts: []string{p.AccountID}, actions: true}, func(m *memstore.Store) error { out, err = m.PlatformAdminActions(ctx, p); return err })
	return
}

func (s *Store) CreateAccessToken(ctx context.Context, p core.Principal, name string, lifetime time.Duration) (core.AccessToken, string, error) {
	now := time.Now().UTC()
	token, secret, err := core.NewAccessToken(name, lifetime, now)
	if err != nil {
		return core.AccessToken{}, "", err
	}
	err = s.UpdateAs(ctx, p, func(a *core.Account) error { return core.AddAccessToken(a, token, now) })
	if err != nil {
		return core.AccessToken{}, "", err
	}
	return token, secret, nil
}

func (s *Store) RevokeAccessToken(ctx context.Context, p core.Principal, id string) error {
	return s.UpdateAs(ctx, p, func(a *core.Account) error { return core.RemoveAccessToken(a, id) })
}

func (s *Store) AccessTokenPrincipal(ctx context.Context, secret string) (out core.Principal, err error) {
	if !strings.HasPrefix(secret, core.AccessTokenPrefix) {
		return out, core.ErrUnauthorized
	}
	err = s.readCore(ctx, coreLoad{tokenHash: core.HashAccessToken(secret)}, func(m *memstore.Store) error { out, err = m.AccessTokenPrincipal(ctx, secret); return err })
	if errors.Is(err, core.ErrNotFound) && !errors.Is(err, core.ErrUnavailable) {
		err = core.ErrUnauthorized
	}
	return
}
