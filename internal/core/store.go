package core

import (
	"crypto/rand"
	"encoding/hex"
	"maps"
	"reflect"
	"strings"
	"sync"
	"time"
)

// Store is one process's demo state. Updates commit an isolated copy under a
// lock, so failed callbacks cannot publish partial mutations.
type Store struct {
	mu            sync.RWMutex
	accounts      map[string]Account
	byEmail       map[string]string
	organizations map[string]Organization
	billingEvents map[string]struct{}
	plans         map[string]Plan
	adminActions  []AdminAction
}

func NewStore() *Store {
	return &Store{
		accounts: make(map[string]Account), byEmail: make(map[string]string),
		organizations: make(map[string]Organization), billingEvents: make(map[string]struct{}),
		plans: map[string]Plan{
			"plan_free":     {ID: "plan_free", Key: "free", Name: "Free", Visible: true},
			"plan_starter":  {ID: "plan_starter", Key: "starter", Name: "Starter", PriceCents: 500, AnnualPriceCents: 5000, TrialDays: 30, Visible: true},
			"plan_pro":      {ID: "plan_pro", Key: "pro", Name: "Pro", PriceCents: 5000, AnnualPriceCents: 50000, TrialDays: 30, Visible: true},
			"plan_business": {ID: "plan_business", Key: "business", Name: "Business", PriceCents: 9900, AnnualPriceCents: 99000, TrialDays: 30, Visible: true},
		},
	}
}

func NewID(prefix string) string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic("crypto/rand unavailable: " + err.Error())
	}
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
	s.accounts[a.ID] = cloneAccount(a)
	s.byEmail[a.User.Email] = a.ID
	for id, org := range a.Organizations {
		s.organizations[id] = cloneOrganization(org)
	}
	return nil
}

func (s *Store) Snapshot(accountID string) (Account, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[accountID]
	if !ok {
		return Account{}, false
	}
	return s.materialize(a), true
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
	a, ok := s.Snapshot(p.AccountID)
	if !ok {
		return Account{}, ErrNotFound
	}
	if err := authorize(a, p); err != nil {
		return Account{}, err
	}
	return a, nil
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
	next := s.materialize(current)
	if err := authorize(next, p); err != nil {
		return err
	}
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
	s.accounts[next.ID] = cloneAccount(next)
	for id, org := range next.Organizations {
		s.organizations[id] = cloneOrganization(org)
	}
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
	if err := authorize(s.materialize(a), p); err != nil {
		return err
	}
	return s.updateLocked(p.AccountID, func(next *Account) error {
		before := cloneAccount(*next)
		if err := mutate(next); err != nil {
			return err
		}
		for id, old := range before.Organizations {
			if id == p.OrganizationID {
				continue
			}
			updated, ok := next.Organizations[id]
			if !ok || !reflect.DeepEqual(old, updated) {
				return ErrForbidden
			}
		}
		for id, added := range next.Organizations {
			if _, existed := before.Organizations[id]; existed {
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
	}
	s.accounts[accountID] = cloneAccount(next)
	for id, org := range next.Organizations {
		s.organizations[id] = cloneOrganization(org)
	}
	s.repairSelectionsLocked()
	return nil
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
			for id, org := range a.Organizations {
				if org.Kind == "personal" {
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
			s.accounts[accountID] = cloneAccount(a)
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

func (s *Store) materialize(a Account) Account {
	n := cloneAccount(a)
	for id := range n.Organizations {
		if org, ok := s.organizations[id]; ok {
			n.Organizations[id] = cloneOrganization(org)
		} else {
			delete(n.Organizations, id)
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
	n := a
	n.DemoSecurity.PendingRecoveryCodes = append([]string(nil), a.DemoSecurity.PendingRecoveryCodes...)
	n.DemoSecurity.Passkeys = append([]DemoPasskey(nil), a.DemoSecurity.Passkeys...)
	n.Notifications = append([]Notification(nil), a.Notifications...)
	n.AuditEvents = append([]AuditEvent(nil), a.AuditEvents...)
	n.Organizations = make(map[string]Organization, len(a.Organizations))
	for id, org := range a.Organizations {
		n.Organizations[id] = cloneOrganization(org)
	}
	return n
}

func cloneOrganization(org Organization) Organization {
	o := org
	o.CheckoutSessions = make(map[string]CheckoutSession, len(org.CheckoutSessions))
	for k, v := range org.CheckoutSessions {
		o.CheckoutSessions[k] = v
	}
	o.Members = make(map[string]Member, len(org.Members))
	for k, v := range org.Members {
		o.Members[k] = v
	}
	o.Invitations = make(map[string]Invitation, len(org.Invitations))
	for k, v := range org.Invitations {
		o.Invitations[k] = v
	}
	o.Billing.Invoices = append([]Invoice(nil), org.Billing.Invoices...)
	o.Projects = make(map[string]Project, len(org.Projects))
	for pid, project := range org.Projects {
		p := project
		p.Regions = append([]string(nil), project.Regions...)
		p.AuthInstance = cloneAuthInstance(project.AuthInstance)
		p.SavedQueries = make(map[string]SavedQuery, len(project.SavedQueries))
		for queryID, query := range project.SavedQueries {
			p.SavedQueries[queryID] = query
		}
		p.SharedResultConfigs = maps.Clone(project.SharedResultConfigs)
		p.BackupPolicies = make(map[string]BackupPolicy, len(project.BackupPolicies))
		for databaseID, policy := range project.BackupPolicies {
			p.BackupPolicies[databaseID] = policy
		}
		p.APIKeys = make(map[string]APIKey, len(project.APIKeys))
		for keyID, key := range project.APIKeys {
			key.Scopes = append([]string(nil), key.Scopes...)
			p.APIKeys[keyID] = key
		}
		p.Databases = make(map[string]Database, len(project.Databases))
		for dbID, database := range project.Databases {
			db := database
			db.IndexRecommendations = make([]IndexRecommendation, len(database.IndexRecommendations))
			for i, recommendation := range database.IndexRecommendations {
				recommendation.Fields = append([]string(nil), recommendation.Fields...)
				db.IndexRecommendations[i] = recommendation
			}
			db.IndexBuilds = append([]IndexBuild(nil), database.IndexBuilds...)
			db.Backups = make([]Backup, len(database.Backups))
			for i, backup := range database.Backups {
				copyBackup := backup
				copyBackup.Documents = cloneDocuments(backup.Documents)
				copyBackup.Schemas = cloneSchemas(backup.Schemas)
				copyBackup.Indexes = cloneIndexes(backup.Indexes)
				db.Backups[i] = copyBackup
			}
			db.Schemas = cloneSchemas(database.Schemas)
			db.Indexes = cloneIndexes(database.Indexes)
			db.Documents = cloneDocuments(database.Documents)
			db.DocumentHistory = cloneHistory(database.DocumentHistory)
			p.Databases[dbID] = db
		}
		o.Projects[pid] = p
	}
	return o
}

func cloneAuthInstance(instance AuthInstance) AuthInstance {
	copyInstance := instance
	copyInstance.Resources = make([]AuthResource, len(instance.Resources))
	copyInstance.AuditEvents = append([]AuditEvent(nil), instance.AuditEvents...)
	for i, resource := range instance.Resources {
		copyResource := resource
		copyResource.Values = make(map[string]string, len(resource.Values))
		for key, value := range resource.Values {
			copyResource.Values[key] = value
		}
		copyInstance.Resources[i] = copyResource
	}
	return copyInstance
}

func cloneDocuments(documents map[string]map[string][]byte) map[string]map[string][]byte {
	out := make(map[string]map[string][]byte, len(documents))
	for collection, docs := range documents {
		copyDocs := make(map[string][]byte, len(docs))
		for id, body := range docs {
			copyDocs[id] = append([]byte(nil), body...)
		}
		out[collection] = copyDocs
	}
	return out
}

func cloneHistory(history map[string]map[string][][]byte) map[string]map[string][][]byte {
	out := make(map[string]map[string][][]byte, len(history))
	for collection, docs := range history {
		copyDocs := make(map[string][][]byte, len(docs))
		for id, revisions := range docs {
			copyRevisions := make([][]byte, len(revisions))
			for i, body := range revisions {
				copyRevisions[i] = append([]byte(nil), body...)
			}
			copyDocs[id] = copyRevisions
		}
		out[collection] = copyDocs
	}
	return out
}

func cloneSchemas(schemas map[string][]byte) map[string][]byte {
	out := make(map[string][]byte, len(schemas))
	for collection, schema := range schemas {
		out[collection] = append([]byte(nil), schema...)
	}
	return out
}

func cloneIndexes(indexes map[string][]string) map[string][]string {
	out := make(map[string][]string, len(indexes))
	for collection, fields := range indexes {
		out[collection] = append([]string(nil), fields...)
	}
	return out
}
