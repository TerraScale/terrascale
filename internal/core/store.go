package core

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"maps"
	"slices"
	"strings"
	"time"
)

// ErrUnavailable wraps a failure of the backing database itself, such as a
// lost connection or an exhausted retry budget, as opposed to the domain
// errors above, which describe the request. Store implementations backed by
// an external database wrap transport and server failures with it; the
// in-memory store never returns it.
var ErrUnavailable = errors.New("store unavailable")

// Store is the dashboard's persistent state: accounts with their users,
// organization memberships, personal access tokens, notifications and audit
// events; organizations with their projects, databases, keys, invitations,
// billing and checkout sessions; the plan catalog; and the platform admin
// action log. memstore.Store keeps it in process memory for the demo; see
// docs/store-contract.md for how each invariant maps onto TerraBase.
//
// Conventions every implementation follows:
//
//   - Every method takes the request context first.
//   - A lookup that finds nothing returns ErrNotFound; there are no (T, bool)
//     results. Domain failures use the sentinel errors in this package
//     (ErrNotFound, ErrConflict, ErrForbidden, ErrUnauthorized, ErrInvalid,
//     ErrDatabaseLimit); database failures wrap ErrUnavailable.
//   - Values returned are isolated copies the caller may keep and modify.
//   - The mutate and read callbacks of the aggregate methods (Update,
//     UpdateAs, UpdateAsWithPlan, UpdateAccountAsAdmin, UpdateOrganization,
//     ViewAs) are pure functions of the aggregate they receive and of values
//     the caller captured before the call. They must not call the Store,
//     perform I/O, or keep references to their argument, and a mutate
//     callback may run more than once: an implementation may load the
//     aggregate, apply mutate to a copy and commit it conditionally,
//     retrying from a fresh load when another writer committed first. The
//     side effects of a mutate run that did not commit must be discarded by
//     the caller, so mutate should only write to the aggregate and to
//     variables it overwrites (never appends to) on every run.
type Store interface {
	// InsertAccount loads a complete account fixture. It enforces unique
	// account, email and organization identities.
	InsertAccount(ctx context.Context, a Account) error
	// CreateAccount registers user with a Personal organization and project,
	// as NewAccount builds them, and returns the stored account.
	CreateAccount(ctx context.Context, user User) (Account, error)
	// FindByEmail returns the account registered with email, compared after
	// NormalizeEmail.
	FindByEmail(ctx context.Context, email string) (Account, error)
	// FindBySubject returns the account whose user signs in as the ts-auth
	// subject. Subjects are unique, and once set a user's never changes.
	FindBySubject(ctx context.Context, subject string) (Account, error)

	// SnapshotAs returns the principal's account after AuthorizeAccount.
	SnapshotAs(ctx context.Context, p Principal) (Account, error)
	// ViewAs calls read with the principal's authorized account. The account
	// may share the store's memory, so read must not retain or modify
	// anything reachable from it.
	ViewAs(ctx context.Context, p Principal, read func(Account) error) error
	// ScopedPrincipal narrows p to the organization and project scope names,
	// as ScopePrincipal does, without changing the saved selection.
	ScopedPrincipal(ctx context.Context, p Principal, scope Scope) (Principal, error)
	// CurrentPrincipal returns the principal for the account's saved
	// selection when the account still belongs to userID.
	CurrentPrincipal(ctx context.Context, accountID, userID string) (Principal, error)
	// SessionPrincipal resolves a session issued for authVersion to the
	// account's current principal; it fails with ErrUnauthorized once the
	// credentials changed or the selection no longer authorizes.
	SessionPrincipal(ctx context.Context, accountID, userID string, authVersion uint64) (Principal, error)

	// Update applies a trusted change to an account and every organization
	// it belongs to. It is for authentication and server integrations; HTTP
	// handlers use UpdateAs.
	Update(ctx context.Context, accountID string, mutate func(*Account) error) error
	// UpdateAs applies the principal's change after AuthorizeAccount and
	// rejects, with ErrForbidden, any change to an organization other than
	// the principal's selected one, except creating organizations the
	// principal owns.
	UpdateAs(ctx context.Context, p Principal, mutate func(*Account) error) error
	// UpdateAsWithPlan is UpdateAs with the canonical plan planID read in the
	// same transaction, so the plan cannot change or disappear between
	// validation and commit.
	UpdateAsWithPlan(ctx context.Context, p Principal, planID string, mutate func(*Account, Plan) error) error
	// ChangeEmailAs changes the account email and its unique index if the
	// password hash still equals expectedPasswordHash.
	ChangeEmailAs(ctx context.Context, p Principal, email, expectedPasswordHash string) error

	// FindInvitation returns a pending, unexpired invitation by its token
	// hash, with its token hash cleared, and the organization's identity
	// only.
	FindInvitation(ctx context.Context, tokenHash string) (Organization, Invitation, error)
	// AcceptInvitation joins the principal's account to the invitation's
	// organization and selects it, within the plan's member limit.
	AcceptInvitation(ctx context.Context, p Principal, tokenHash string) (Organization, error)
	// DeclineInvitation marks the principal's invitation declined.
	DeclineInvitation(ctx context.Context, p Principal, tokenHash string) (Organization, error)

	// Organization returns one organization for trusted integrations that
	// act without a principal, such as Stripe syncs.
	Organization(ctx context.Context, organizationID string) (Organization, error)
	// UpdateOrganization applies a trusted change to one organization. The
	// callback receives the plan catalog keyed by both plan ID and key.
	UpdateOrganization(ctx context.Context, organizationID string, mutate func(*Organization, map[string]Plan) error) error

	// Plans returns the plan catalog in no particular order.
	Plans(ctx context.Context) ([]Plan, error)
	// Plan returns the plan whose ID or key is idOrKey.
	Plan(ctx context.Context, idOrKey string) (Plan, error)
	// UpsertPlanAs creates or replaces a plan; plan keys never change and
	// never collide with another plan's ID or key.
	UpsertPlanAs(ctx context.Context, p Principal, plan Plan) error
	// DeletePlanAs deletes a plan no organization or open checkout uses.
	DeletePlanAs(ctx context.Context, p Principal, planID string) error

	// AuthorizePlatformAdmin reports whether the principal is authorized and
	// a platform administrator.
	AuthorizePlatformAdmin(ctx context.Context, p Principal) error
	// PlatformAccounts returns every account for a platform administrator.
	PlatformAccounts(ctx context.Context, p Principal) ([]Account, error)
	// UpdateAccountAsAdmin is Update on any account for a platform
	// administrator.
	UpdateAccountAsAdmin(ctx context.Context, p Principal, targetAccountID string, mutate func(*Account) error) error
	// RecordPlatformAdminAction appends one record to the admin action log.
	RecordPlatformAdminAction(ctx context.Context, p Principal, action, target, reason string) error
	// SetPlatformAdmin grants or revokes platform administration for one
	// account, for operators with direct store access, and records the change
	// in the admin action log in the same commit. It reports whether anything
	// changed; repeating a grant or a revoke is a no-op.
	SetPlatformAdmin(ctx context.Context, accountID string, admin bool) (bool, error)
	// PlatformAdminActions returns the admin action log, oldest first.
	PlatformAdminActions(ctx context.Context, p Principal) ([]AdminAction, error)

	// CreateAccessToken issues a personal access token, as NewAccessToken
	// builds it, and returns it with its one-time secret. An account holds
	// at most MaxAccessTokens unexpired tokens.
	CreateAccessToken(ctx context.Context, p Principal, name string, lifetime time.Duration) (AccessToken, string, error)
	// RevokeAccessToken deletes one of the principal's tokens.
	RevokeAccessToken(ctx context.Context, p Principal, id string) error
	// AccessTokenPrincipal resolves an unexpired token secret to its
	// account's current principal, or fails with ErrUnauthorized.
	AccessTokenPrincipal(ctx context.Context, secret string) (Principal, error)
}

// Authorize reports whether the principal still matches its account and
// selected scope.
func Authorize(ctx context.Context, s Store, p Principal) error {
	return s.ViewAs(ctx, p, func(Account) error { return nil })
}

// Can returns nil when the principal is authorized and its selected
// organization satisfies allow, such as Organization.CanManage. Every domain
// failure, including a stale principal, is reported as ErrForbidden; only a
// database failure (ErrUnavailable) is returned as is.
func Can(ctx context.Context, s Store, p Principal, allow func(Organization, string) bool) error {
	err := s.ViewAs(ctx, p, func(a Account) error {
		if !allow(a.Organizations[p.OrganizationID], p.UserID) {
			return ErrForbidden
		}
		return nil
	})
	if err != nil && !errors.Is(err, ErrUnavailable) {
		return ErrForbidden
	}
	return err
}

func NewID(prefix string) string {
	var b [16]byte
	rand.Read(b[:])
	return prefix + "_" + hex.EncodeToString(b[:])
}

// NewAccount builds the account Store.CreateAccount registers: the user with
// a Personal organization, its default Personal project, and both selected.
func NewAccount(user User) (Account, error) {
	user.Email = NormalizeEmail(user.Email)
	if user.Email == "" || strings.TrimSpace(user.Name) == "" || user.PasswordHash == "" {
		return Account{}, ErrInvalid
	}
	if user.ID == "" {
		user.ID = NewID("usr")
	}
	if user.CreatedAt.IsZero() {
		user.CreatedAt = time.Now().UTC()
	}
	if user.Role == "" {
		user.Role = "owner"
	}
	org := Organization{
		ID: NewID("org"), Name: "Personal", Kind: "personal", Status: "active",
		OwnerUserID: user.ID, PlanID: "plan_free", BillingStatus: "active",
		Projects: make(map[string]Project), Members: map[string]Member{
			user.ID: {UserID: user.ID, Email: user.Email, Name: user.Name, Role: "owner", Status: "active", CreatedAt: time.Now().UTC()},
		}, Invitations: make(map[string]Invitation), CheckoutSessions: make(map[string]CheckoutSession),
		CreatedAt: time.Now().UTC(),
	}
	project := Project{
		ID: NewID("prj"), OrganizationID: org.ID, Name: "Personal", Region: "GRU",
		Regions: []string{"GRU"}, Status: "active", IsDefault: true,
		Databases: make(map[string]Database), APIKeys: make(map[string]APIKey),
		SavedQueries: make(map[string]SavedQuery), SharedResultConfigs: make(map[string]SharedResultConfig),
		BackupPolicies: make(map[string]BackupPolicy),
		CreatedAt:      time.Now().UTC(),
	}
	org.Projects[project.ID] = project
	return Account{
		ID: NewID("acct"), User: user, Organizations: map[string]Organization{org.ID: org},
		ActiveOrganizationID: org.ID, ActiveProjectID: project.ID,
		OnboardingStep: "payment-method", CreatedAt: time.Now().UTC(),
	}, nil
}

// DefaultPlans is the catalog a new store starts with. Free's limits are
// the public Hobby plan's (terrascale.tech/pricing.json). StorageMiB counts
// a label's GB as a GiB, so utilization never runs out before the label.
func DefaultPlans() []Plan {
	return []Plan{
		{ID: "plan_free", Key: "free", Name: "Free", Visible: true, MaxDatabases: 1, OperationsPerMonth: 100_000, StorageMiB: 100, StorageLabel: "0.1 GB-region storage"},
		{ID: "plan_starter", Key: "starter", Name: "Starter", PriceCents: 500, AnnualPriceCents: 5000, TrialDays: 30, Visible: true, MaxDatabases: 5, MaxMembers: 5, OperationsPerMonth: 1_000_000, StorageMiB: 5 << 10, StorageLabel: "5 GB-region storage"},
		{ID: "plan_pro", Key: "pro", Name: "Pro", PriceCents: 5000, AnnualPriceCents: 50000, TrialDays: 30, Visible: true, MaxDatabases: 25, MaxMembers: 25, OperationsPerMonth: 100_000_000, StorageMiB: 25 << 10, StorageLabel: "25 GB-region storage"},
		{ID: "plan_business", Key: "business", Name: "Business", PriceCents: 9900, AnnualPriceCents: 99000, TrialDays: 30, Visible: true, MaxDatabases: 100, MaxMembers: 100, OperationsPerMonth: 50_000_000, StorageMiB: 5 << 20, StorageLabel: "5 TB-region storage"},
	}
}

// SyncMemberships copies the user's current name and email into their member
// record in each of their organizations, which keep them for display, so a
// change made in one organization reaches every other.
func SyncMemberships(a *Account) {
	for _, org := range a.Organizations {
		if member, ok := org.Members[a.User.ID]; ok {
			member.Name, member.Email = a.User.Name, a.User.Email
			org.Members[a.User.ID] = member
		}
	}
}

// MemberActive reports whether userID is an active member, or the owner, of
// org.
func MemberActive(org Organization, userID string) bool {
	member, ok := org.Members[userID]
	if !ok {
		return org.OwnerUserID == userID
	}
	return member.Status == "" || strings.EqualFold(member.Status, "active")
}

// AuthorizeAccount reports whether p still matches a, its credential version
// and its selected organization and project. a holds the account's live
// organizations.
func AuthorizeAccount(a Account, p Principal) error {
	if p.AccountID == "" || a.ID != p.AccountID || a.User.ID != p.UserID {
		return ErrForbidden
	}
	if a.AuthVersion != p.AuthVersion {
		return ErrUnauthorized
	}
	if p.OrganizationID == "" {
		if p.ProjectID != "" {
			return ErrForbidden
		}
		return nil
	}
	org, err := a.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		// A previously valid scope whose membership was removed is an
		// authorization failure, even though the repaired aggregate no longer
		// contains that organization.
		if errors.Is(err, ErrNotFound) {
			return ErrForbidden
		}
		return err
	}
	if p.ProjectID != "" {
		if _, err := a.ProjectFor(p, org.ID, p.ProjectID); err != nil {
			return err
		}
	}
	return nil
}

// CloneAccount deep-copies an account and its organizations.
func CloneAccount(a Account) Account {
	n := CloneAccountFields(a)
	n.Organizations = make(map[string]Organization, len(a.Organizations))
	for id, org := range a.Organizations {
		n.Organizations[id] = CloneOrganization(org)
	}
	return n
}

// CloneAccountFields deep-copies an account's own fields and leaves its
// Organizations nil.
func CloneAccountFields(a Account) Account {
	n := a
	n.DemoSecurity.PendingRecoveryCodes = slices.Clone(a.DemoSecurity.PendingRecoveryCodes)
	n.DemoSecurity.Passkeys = slices.Clone(a.DemoSecurity.Passkeys)
	n.Notifications = slices.Clone(a.Notifications)
	n.AuditEvents = slices.Clone(a.AuditEvents)
	n.AccessTokens = slices.Clone(a.AccessTokens)
	n.Organizations = nil
	return n
}

// CloneOrganization deep-copies an organization. Its maps are never nil in
// the copy, so cloned organizations compare equal with reflect.DeepEqual
// exactly when their contents are equal.
func CloneOrganization(org Organization) Organization {
	o := org
	o.CheckoutSessions = cloneMap(org.CheckoutSessions)
	o.Members = cloneMap(org.Members)
	o.Invitations = cloneMap(org.Invitations)
	o.Billing.Invoices = slices.Clone(org.Billing.Invoices)
	o.Projects = make(map[string]Project, len(org.Projects))
	for pid, project := range org.Projects {
		p := project
		p.Regions = slices.Clone(project.Regions)
		p.AuthInstance = cloneAuthInstance(project.AuthInstance)
		p.SavedQueries = cloneMap(project.SavedQueries)
		p.SharedResultConfigs = maps.Clone(project.SharedResultConfigs)
		p.BackupPolicies = cloneMap(project.BackupPolicies)
		p.APIKeys = make(map[string]APIKey, len(project.APIKeys))
		for keyID, key := range project.APIKeys {
			key.Scopes = slices.Clone(key.Scopes)
			p.APIKeys[keyID] = key
		}
		p.Databases = make(map[string]Database, len(project.Databases))
		for dbID, database := range project.Databases {
			db := database
			db.Regions = slices.Clone(database.Regions)
			db.IndexRecommendations = make([]IndexRecommendation, len(database.IndexRecommendations))
			for i, recommendation := range database.IndexRecommendations {
				recommendation.Fields = slices.Clone(recommendation.Fields)
				db.IndexRecommendations[i] = recommendation
			}
			db.IndexBuilds = slices.Clone(database.IndexBuilds)
			db.Backups = make([]Backup, len(database.Backups))
			for i, backup := range database.Backups {
				backup.Documents = CloneDocuments(backup.Documents)
				backup.Schemas = CloneSchemas(backup.Schemas)
				backup.Indexes = CloneIndexes(backup.Indexes)
				db.Backups[i] = backup
			}
			db.Schemas = CloneSchemas(database.Schemas)
			db.Indexes = CloneIndexes(database.Indexes)
			db.Documents = CloneDocuments(database.Documents)
			db.DocumentHistory = CloneHistory(database.DocumentHistory)
			p.Databases[dbID] = db
		}
		o.Projects[pid] = p
	}
	return o
}

func cloneAuthInstance(instance AuthInstance) AuthInstance {
	copyInstance := instance
	copyInstance.Resources = make([]AuthResource, len(instance.Resources))
	copyInstance.AuditEvents = slices.Clone(instance.AuditEvents)
	for i, resource := range instance.Resources {
		resource.Values = cloneMap(resource.Values)
		copyInstance.Resources[i] = resource
	}
	return copyInstance
}

// cloneMap copies m into a map that is never nil, which keeps every cloned
// organization in the same shape for UpdateAs's comparison.
func cloneMap[K comparable, V any](m map[K]V) map[K]V {
	out := make(map[K]V, len(m))
	maps.Copy(out, m)
	return out
}

// CloneDocuments deep-copies a collection -> document ID -> JSON map.
func CloneDocuments(documents map[string]map[string][]byte) map[string]map[string][]byte {
	out := make(map[string]map[string][]byte, len(documents))
	for collection, docs := range documents {
		copyDocs := make(map[string][]byte, len(docs))
		for id, body := range docs {
			copyDocs[id] = slices.Clone(body)
		}
		out[collection] = copyDocs
	}
	return out
}

// CloneHistory deep-copies a collection -> document ID -> revisions map.
func CloneHistory(history map[string]map[string][][]byte) map[string]map[string][][]byte {
	out := make(map[string]map[string][][]byte, len(history))
	for collection, docs := range history {
		copyDocs := make(map[string][][]byte, len(docs))
		for id, revisions := range docs {
			copyRevisions := make([][]byte, len(revisions))
			for i, body := range revisions {
				copyRevisions[i] = slices.Clone(body)
			}
			copyDocs[id] = copyRevisions
		}
		out[collection] = copyDocs
	}
	return out
}

// CloneSchemas deep-copies a collection -> schema JSON map.
func CloneSchemas(schemas map[string][]byte) map[string][]byte {
	out := make(map[string][]byte, len(schemas))
	for collection, schema := range schemas {
		out[collection] = slices.Clone(schema)
	}
	return out
}

// CloneIndexes deep-copies a collection -> indexed fields map.
func CloneIndexes(indexes map[string][]string) map[string][]string {
	out := make(map[string][]string, len(indexes))
	for collection, fields := range indexes {
		out[collection] = slices.Clone(fields)
	}
	return out
}
