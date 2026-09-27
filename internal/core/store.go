package core

import (
	"crypto/rand"
	"encoding/hex"
	"maps"
	"reflect"
	"slices"
	"strings"
	"sync"
	"time"
)

// Store is one process's demo state. Updates commit an isolated copy under a
// lock, so failed callbacks cannot publish partial mutations.
//
// Organizations live only in organizations; an entry in accounts records the
// account's own fields and the IDs of the organizations it belongs to, so
// shared tenants are stored and copied once.
type Store struct {
	mu            sync.RWMutex
	accounts      map[string]Account
	byEmail       map[string]string
	organizations map[string]Organization
	plans         map[string]Plan
	adminActions  []AdminAction
}

func NewStore() *Store {
	return &Store{
		accounts: make(map[string]Account), byEmail: make(map[string]string),
		organizations: make(map[string]Organization),
		plans: map[string]Plan{
			"plan_free":     {ID: "plan_free", Key: "free", Name: "Free", Visible: true, MaxDatabases: 1},
			"plan_starter":  {ID: "plan_starter", Key: "starter", Name: "Starter", PriceCents: 500, AnnualPriceCents: 5000, TrialDays: 30, Visible: true, MaxDatabases: 5, MaxMembers: 5, OperationsPerMonth: 1_000_000, StorageLabel: "5 GB-region storage"},
			"plan_pro":      {ID: "plan_pro", Key: "pro", Name: "Pro", PriceCents: 5000, AnnualPriceCents: 50000, TrialDays: 30, Visible: true, MaxDatabases: 25, MaxMembers: 25, OperationsPerMonth: 100_000_000, StorageLabel: "25 GB-region storage"},
			"plan_business": {ID: "plan_business", Key: "business", Name: "Business", PriceCents: 9900, AnnualPriceCents: 99000, TrialDays: 30, Visible: true, MaxDatabases: 100, MaxMembers: 100, OperationsPerMonth: 50_000_000, StorageLabel: "5 TB-region storage"},
		},
	}
}

func NewID(prefix string) string {
	var b [16]byte
	rand.Read(b[:])
	return prefix + "_" + hex.EncodeToString(b[:])
}

func (s *Store) CreateAccount(user User) (Account, error) {
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
	a := Account{
		ID: NewID("acct"), User: user, Organizations: map[string]Organization{org.ID: org},
		ActiveOrganizationID: org.ID, ActiveProjectID: project.ID,
		OnboardingStep: "payment-method", CreatedAt: time.Now().UTC(),
	}
	if err := s.InsertAccount(a); err != nil {
		return Account{}, err
	}
	return cloneAccount(a), nil
}

// InsertAccount loads a complete fixture. It enforces unique account and email
// identities and never retains caller-owned maps or slices.
func (s *Store) InsertAccount(a Account) error {
	a.User.Email = NormalizeEmail(a.User.Email)
	if a.ID == "" || a.User.ID == "" || a.User.Email == "" {
		return ErrInvalid
	}
	if a.Organizations == nil {
		a.Organizations = make(map[string]Organization)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.accounts[a.ID]; ok {
		return ErrConflict
	}
	if _, ok := s.byEmail[a.User.Email]; ok {
		return ErrConflict
	}
	for id := range a.Organizations {
		if _, exists := s.organizations[id]; exists {
			return ErrConflict
		}
	}
	s.putLocked(a)
	s.byEmail[a.User.Email] = a.ID
	return nil
}

func (s *Store) FindByEmail(email string) (Account, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[s.byEmail[NormalizeEmail(email)]]
	if !ok {
		return Account{}, false
	}
	return s.materialize(a), true
}

func (s *Store) SnapshotAs(p Principal) (Account, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return Account{}, ErrNotFound
	}
	if err := authorize(s.viewLocked(a), p); err != nil {
		return Account{}, err
	}
	return s.materialize(a), nil
}

// ViewAs calls read with the principal's authorized account while holding the
// read lock. The account shares the store's maps and slices, so read must not
// retain or modify anything reachable from it.
func (s *Store) ViewAs(p Principal, read func(Account) error) error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return ErrNotFound
	}
	view := s.viewLocked(a)
	if err := authorize(view, p); err != nil {
		return err
	}
	return read(view)
}

// Authorize reports whether the principal still matches its account and
// selected scope.
func (s *Store) Authorize(p Principal) error {
	return s.ViewAs(p, func(Account) error { return nil })
}

// Can reports whether the principal is authorized and its selected
// organization satisfies allow, such as Organization.CanManage.
func (s *Store) Can(p Principal, allow func(Organization, string) bool) bool {
	return s.ViewAs(p, func(a Account) error {
		if !allow(a.Organizations[p.OrganizationID], p.UserID) {
			return ErrForbidden
		}
		return nil
	}) == nil
}

// CurrentPrincipal returns the account's principal for its current selection
// when the account still belongs to userID.
func (s *Store) CurrentPrincipal(accountID, userID string) (Principal, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[accountID]
	if !ok || a.User.ID != userID {
		return Principal{}, false
	}
	return s.viewLocked(a).Principal(), true
}

// SessionPrincipal resolves a session's principal for the credential version
// it was issued with, without copying the account.
func (s *Store) SessionPrincipal(accountID, userID string, authVersion uint64) (Principal, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[accountID]
	if !ok || a.User.ID != userID || a.AuthVersion != authVersion {
		return Principal{}, false
	}
	view := s.viewLocked(a)
	p := view.Principal()
	if authorize(view, p) != nil {
		return Principal{}, false
	}
	return p, true
}

// Update is for authentication and trusted server integration. HTTP feature
// handlers should use UpdateAs, which rechecks principal scope under this lock.
func (s *Store) Update(accountID string, mutate func(*Account) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.updateLocked(accountID, mutate)
}

func (s *Store) UpdateAs(p Principal, mutate func(*Account) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.updateAsLocked(p, mutate)
}

// ChangeEmailAs updates the account email and its lookup index in one scoped
// transaction. The expected hash binds the change to the password verified by
// the caller even if that credential changes between verification and commit.
func (s *Store) ChangeEmailAs(p Principal, email, expectedPasswordHash string) error {
	email = NormalizeEmail(email)
	if email == "" || expectedPasswordHash == "" {
		return ErrInvalid
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.accounts[p.AccountID]
	if !ok {
		return ErrNotFound
	}
	if err := authorize(s.viewLocked(current), p); err != nil {
		return err
	}
	next := s.materialize(current)
	if next.User.PasswordHash != expectedPasswordHash {
		return ErrConflict
	}
	if accountID, exists := s.byEmail[email]; exists && accountID != next.ID {
		return ErrConflict
	}
	oldEmail := next.User.Email
	if oldEmail == email {
		return nil
	}
	next.User.Email = email
	for id, org := range next.Organizations {
		if member, ok := org.Members[p.UserID]; ok {
			member.Email = email
			org.Members[p.UserID] = member
			next.Organizations[id] = org
		}
	}
	s.putLocked(next)
	delete(s.byEmail, oldEmail)
	s.byEmail[email] = next.ID
	return nil
}

// UpdateAsWithPlan resolves a canonical plan under the same lock as the scoped
// account update. A caller can validate the plan and commit its account change
// without a plan edit or deletion occurring between those operations.
func (s *Store) UpdateAsWithPlan(p Principal, planID string, mutate func(*Account, Plan) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.updateAsLocked(p, func(account *Account) error {
		plan, ok := s.plans[planID]
		if !ok {
			return ErrNotFound
		}
		return mutate(account, plan)
	})
}

func (s *Store) updateAsLocked(p Principal, mutate func(*Account) error) error {
	a, ok := s.accounts[p.AccountID]
	if !ok {
		return ErrNotFound
	}
	if err := authorize(s.viewLocked(a), p); err != nil {
		return err
	}
	// The committed organizations are the state mutate started from, so they
	// serve as the "before" image without copying the account again.
	existed := func(id string) bool {
		_, member := a.Organizations[id]
		_, live := s.organizations[id]
		return member && live
	}
	return s.updateLocked(p.AccountID, func(next *Account) error {
		if err := mutate(next); err != nil {
			return err
		}
		for id := range a.Organizations {
			old, live := s.organizations[id]
			if id == p.OrganizationID || !live {
				continue
			}
			updated, ok := next.Organizations[id]
			if !ok || !reflect.DeepEqual(old, updated) {
				return ErrForbidden
			}
		}
		for id, added := range next.Organizations {
			if existed(id) {
				continue
			}
			member, ok := added.Members[p.UserID]
			if added.ID != id || added.OwnerUserID != p.UserID || !ok || member.Role != "owner" {
				return ErrForbidden
			}
		}
		return nil
	})
}

func (s *Store) updateLocked(accountID string, mutate func(*Account) error) error {
	current, ok := s.accounts[accountID]
	if !ok {
		return ErrNotFound
	}
	next := s.materialize(current)
	if err := mutate(&next); err != nil {
		return err
	}
	if next.ID != current.ID || next.User.ID != current.User.ID || NormalizeEmail(next.User.Email) != current.User.Email {
		return ErrInvalid
	}
	if next.User.PasswordHash == "" {
		return ErrInvalid
	}
	if next.User.PasswordHash != current.User.PasswordHash {
		next.AuthVersion = current.AuthVersion + 1
	} else {
		next.AuthVersion = current.AuthVersion
	}
	for id, org := range next.Organizations {
		if !s.providerUniqueLocked(id, org) {
			return ErrConflict
		}
		if !s.withinDatabaseLimitLocked(id, org) {
			return ErrDatabaseLimit
		}
	}
	s.putLocked(next)
	s.repairSelectionsLocked()
	return nil
}

// withinDatabaseLimitLocked reports whether org may be committed with its
// databases. Whatever route adds a database, the organization must stay
// within its plan's MaxDatabases; one already past it, such as after a
// downgrade, keeps what it has but cannot gain more.
func (s *Store) withinDatabaseLimitLocked(id string, org Organization) bool {
	limit := s.plans[org.PlanID].MaxDatabases
	count := org.DatabaseCount()
	return limit <= 0 || count <= limit || count <= s.organizations[id].DatabaseCount()
}

func (s *Store) providerUniqueLocked(id string, org Organization) bool {
	for otherID, other := range s.organizations {
		if otherID == id {
			continue
		}
		if org.Billing.CustomerID != "" && other.Billing.CustomerID == org.Billing.CustomerID {
			return false
		}
		if org.Billing.SubscriptionID != "" && other.Billing.SubscriptionID == org.Billing.SubscriptionID {
			return false
		}
	}
	return true
}

func (s *Store) repairSelectionsLocked() {
	for accountID, a := range s.accounts {
		if a.ActiveOrganizationID == "" {
			continue
		}
		changed := false
		for id := range a.Organizations {
			org, ok := s.organizations[id]
			if !ok || !memberActive(org, a.User.ID) {
				delete(a.Organizations, id)
				changed = true
			}
		}
		if _, ok := a.Organizations[a.ActiveOrganizationID]; !ok {
			a.ActiveOrganizationID = ""
			a.ActiveProjectID = ""
			for id := range a.Organizations {
				if s.organizations[id].Kind == "personal" {
					a.ActiveOrganizationID = id
					break
				}
			}
			if a.ActiveOrganizationID == "" {
				for id := range a.Organizations {
					a.ActiveOrganizationID = id
					break
				}
			}
			if org, ok := s.organizations[a.ActiveOrganizationID]; ok {
				for id := range org.Projects {
					a.ActiveProjectID = id
					break
				}
			}
			changed = true
		}
		if changed {
			s.accounts[accountID] = detachAccount(a)
		}
	}
}

func memberActive(org Organization, userID string) bool {
	member, ok := org.Members[userID]
	if !ok {
		return org.OwnerUserID == userID
	}
	return member.Status == "" || strings.EqualFold(member.Status, "active")
}

// putLocked commits a's own fields and memberships to accounts and a copy of
// each of its organizations to organizations.
func (s *Store) putLocked(a Account) {
	s.accounts[a.ID] = detachAccount(a)
	for id, org := range a.Organizations {
		s.organizations[id] = cloneOrganization(org)
	}
}

// materialize returns an isolated copy of a stored account with its live
// organizations.
func (s *Store) materialize(a Account) Account {
	n := cloneAccountFields(a)
	n.Organizations = make(map[string]Organization, len(a.Organizations))
	for id := range a.Organizations {
		if org, ok := s.organizations[id]; ok {
			n.Organizations[id] = cloneOrganization(org)
		}
	}
	return n
}

// viewLocked is materialize without copies, for read-only use under the lock.
func (s *Store) viewLocked(a Account) Account {
	n := a
	n.Organizations = make(map[string]Organization, len(a.Organizations))
	for id := range a.Organizations {
		if org, ok := s.organizations[id]; ok {
			n.Organizations[id] = org
		}
	}
	return n
}

func authorize(a Account, p Principal) error {
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
		return err
	}
	if p.ProjectID != "" {
		if _, err := a.ProjectFor(p, org.ID, p.ProjectID); err != nil {
			return err
		}
	}
	return nil
}

func cloneAccount(a Account) Account {
	n := cloneAccountFields(a)
	n.Organizations = make(map[string]Organization, len(a.Organizations))
	for id, org := range a.Organizations {
		n.Organizations[id] = cloneOrganization(org)
	}
	return n
}

// detachAccount copies a's own fields and keeps only its organization IDs.
func detachAccount(a Account) Account {
	n := cloneAccountFields(a)
	n.Organizations = make(map[string]Organization, len(a.Organizations))
	for id := range a.Organizations {
		n.Organizations[id] = Organization{ID: id}
	}
	return n
}

func cloneAccountFields(a Account) Account {
	n := a
	n.DemoSecurity.PendingRecoveryCodes = slices.Clone(a.DemoSecurity.PendingRecoveryCodes)
	n.DemoSecurity.Passkeys = slices.Clone(a.DemoSecurity.Passkeys)
	n.Notifications = slices.Clone(a.Notifications)
	n.AuditEvents = slices.Clone(a.AuditEvents)
	n.Organizations = nil
	return n
}

func cloneOrganization(org Organization) Organization {
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

// cloneMap copies m into a map that is never nil, which keeps every stored
// organization in the same shape for Store.updateAsLocked's comparison.
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
