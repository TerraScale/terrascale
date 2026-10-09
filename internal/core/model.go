package core

import (
	"context"
	"errors"
	"maps"
	"net/http"
	"regexp"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/sdk/v1"
)

var (
	ErrNotFound     = errors.New("not found")
	ErrConflict     = errors.New("conflict")
	ErrUnauthorized = errors.New("unauthorized")
	ErrForbidden    = errors.New("forbidden")
	ErrInvalid      = errors.New("invalid input")
	// ErrDatabaseLimit rejects a change that would give an organization more
	// databases than its plan allows.
	ErrDatabaseLimit = errors.New("plan database limit reached")
)

// databaseName is the rule every database name follows: 1 to 63 lowercase
// letters, digits, and inner hyphens.
var databaseName = regexp.MustCompile(`^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$`)

// ValidDatabaseName reports whether name follows the database name rule.
func ValidDatabaseName(name string) bool {
	return databaseName.MatchString(name)
}

// Principal is the identity and selected scope for one request. Every feature
// mutation must resolve its target organization and project against this scope.
type Principal struct {
	UserID         string
	AccountID      string
	OrganizationID string
	ProjectID      string
	Role           string
	PlatformAdmin  bool
	AuthVersion    uint64
}

type User struct {
	ID    string
	Email string
	Name  string
	// AuthSubject is the user's ts-auth subject, set when they first sign in
	// through ts-auth and never changed after. TerraBase knows the user by it,
	// so Management calls on their behalf send it as X-TerraBase-User-Id.
	AuthSubject   string
	PasswordHash  string
	Phone         string
	Locale        string
	Role          string
	PlatformAdmin bool
	CreatedAt     time.Time
}

type Account struct {
	ID                   string
	User                 User
	Organizations        map[string]Organization
	ActiveOrganizationID string
	ActiveProjectID      string
	OnboardingCompleted  bool
	OnboardingStep       string
	AuthVersion          uint64
	DemoSecurity         DemoProfileSecurity
	Notifications        []Notification
	AuditEvents          []AuditEvent
	AccessTokens         []AccessToken
	CreatedAt            time.Time
}

// DemoProfileSecurity records profile controls displayed by the in-memory
// dashboard. These records never authenticate a sign-in request.
type DemoProfileSecurity struct {
	MFAEnabled           bool
	PendingTOTPToken     string
	PendingTOTPSecret    string
	PendingRecoveryCodes []string
	Passkeys             []DemoPasskey
}

type DemoPasskey struct {
	ID        string
	Name      string
	CreatedAt time.Time
}

// SessionInfo is a safe, read-only view of a local dashboard session.
type SessionInfo struct {
	ID        string
	Current   bool
	ExpiresAt time.Time
}

type Organization struct {
	ID                     string
	Name                   string
	Kind                   string
	Slug                   string
	Status                 string
	Archived               bool
	DeletionRequested      bool
	PendingOwnerUserID     string
	PendingOwnerExpiresAt  time.Time
	OwnerUserID            string
	PlanID                 string
	BillingStatus          string
	PaymentMethodValidated bool
	BillingRenewal         time.Time
	CheckoutSessions       map[string]CheckoutSession
	Projects               map[string]Project
	Members                map[string]Member
	Invitations            map[string]Invitation
	Billing                Billing
	CreatedAt              time.Time
	AuditRetentionDays     int
}

type Member struct {
	UserID    string
	Email     string
	Name      string
	Role      string
	Status    string
	CreatedAt time.Time
}

type Invitation struct {
	ID             string
	Email          string
	Role           string
	Status         string
	TokenHash      string
	OrganizationID string
	InviterEmail   string
	ExpiresAt      time.Time
	CreatedAt      time.Time
}

type Project struct {
	ID                        string
	OrganizationID            string
	Name                      string
	Region                    string
	BackupRegion              string
	Regions                   []string
	Slug                      string
	Status                    string
	IsDefault                 bool
	Databases                 map[string]Database
	APIKeys                   map[string]APIKey
	SavedQueries              map[string]SavedQuery
	SharedResultConfigs       map[string]SharedResultConfig
	AuthInstance              AuthInstance
	AuthenticationEnabled     bool
	SecuritySettingsUpdatedAt time.Time
	BackupPolicies            map[string]BackupPolicy
	SpendCapCents             int64
	CreatedAt                 time.Time
	// DataKeys are the data API keys TerraBase issued the dashboard for the
	// project's TerraBase project, by Atlas region ID (TerraBase issues one
	// per region); see Deps.Data. Its members never see them.
	// TODO(TS-589, TS-599): ts-auth project tokens replace them.
	DataKeys map[string]DataKey
}

// DataKey is a TerraBase data API key (tb_…) with its ID.
type DataKey struct{ ID, Secret string }

// TerraBaseIDs returns the IDs under which TerraBase knows the project of
// organization orgID. A project mirrored from TerraBase has its own. For any
// other they are derived from the dashboard IDs (name-based UUIDs), so every
// region and every retry agrees on them without recording them.
// TODO(TS-599): ts-auth's organization and project IDs replace them.
func (p Project) TerraBaseIDs(orgID string) (organization, project string) {
	if terraBaseID.MatchString(p.ID) {
		return orgID, p.ID
	}
	return v1.DocumentIDFor("terrabase-organizations", orgID), v1.DocumentIDFor("terrabase-projects", p.ID)
}

type Database struct {
	ID        string
	ProjectID string
	// TerraBaseID is the database's ID in its project's TerraBase project.
	TerraBaseID          string
	Name                 string
	Status               string
	Region               string
	Regions              []string
	Endpoint             string
	CreatedAt            time.Time
	Documents            map[string]map[string][]byte   // collection -> document ID -> JSON bytes
	DocumentHistory      map[string]map[string][][]byte // collection -> document ID -> revisions
	Schemas              map[string][]byte              // collection -> schema JSON
	Indexes              map[string][]string            // collection -> indexed fields
	IndexRecommendations []IndexRecommendation
	IndexBuilds          []IndexBuild
	Backups              []Backup
	BackupSchedule       string
	BackupRetentionDays  int
	SpendCapCents        int64
}

type IndexRecommendation struct {
	ID         string
	DatabaseID string
	Collection string
	Fields     []string
	Reason     string
	State      string
}

type IndexBuild struct {
	ID         string
	Collection string
	Field      string
	Status     string
}

type AdminAction struct {
	ActorUserID string
	Action      string
	Target      string
	Reason      string
	RecordedAt  time.Time
}

type SavedQuery struct {
	ID        string
	Name      string
	SQL       string
	CreatedAt time.Time
}

// SharedResultConfig is a project-owned saved shared-result configuration.
// Provider-managed runtime state is not represented in the demo store.
type SharedResultConfig struct {
	ID         string
	DatabaseID string
	Name       string
	Query      string
	Transport  string
	Status     string
	CreatedAt  time.Time
}

type BackupPolicy struct {
	FullBackupCron        string
	IncrementalBackupCron string
	RetentionDays         int
	Disabled              bool
	UpdatedAt             time.Time
}

type Notification struct {
	ID, OrganizationID, ProjectID, Type, Title, Body, Status, Severity string
	// URL is the dashboard page where the reader can act on it, if any.
	URL       string
	CreatedAt time.Time
	// BillingMonth identifies the UTC usage month (YYYY-MM) for billing alerts.
	BillingMonth string
}

// VisibleTo reports whether n belongs to the principal's selected
// organization and project; account-wide notifications name neither.
func (n Notification) VisibleTo(p Principal) bool {
	return (n.OrganizationID == "" || n.OrganizationID == p.OrganizationID) &&
		(n.ProjectID == "" || n.ProjectID == p.ProjectID)
}

// Unread reports whether n has not been read yet.
func (n Notification) Unread() bool {
	return strings.EqualFold(n.Status, "unread")
}

type AuditEvent struct {
	ID, OrganizationID, ProjectID, Actor, Action, Target, TargetType, Risk, Source string
	OccurredAt                                                                     time.Time
}

type AuthResource struct {
	ID, Section, Status string
	Values              map[string]string
	CreatedAt           time.Time
}

type AuthInstance struct {
	Resources                 []AuthResource
	AuditEvents               []AuditEvent
	Name, LogoURL, ThemeColor string
	Settings                  AuthSettings
}

// AuthSettings are the instance-wide sign-in and multi-factor strategies a
// ts-auth instance offers on its hosted pages. UpdatedAt is zero until an
// administrator first saves them; until then ts-auth defaults apply.
type AuthSettings struct {
	EmailVerification bool // verify the email address at sign-up
	EmailPasscode     bool // one-time email codes
	MagicLink         bool // one-click email links
	Password          bool
	Username          bool
	Passkeys          bool
	TOTP              bool // authenticator-app second factor
	RecoveryCodes     bool
	UpdatedAt         time.Time
}

type Backup struct {
	ID         string
	DatabaseID string
	Status     string
	CreatedAt  time.Time
	SizeBytes  int64
	Documents  map[string]map[string][]byte
	Schemas    map[string][]byte
	Indexes    map[string][]string
}

type APIKey struct {
	ID         string
	Kind       string
	ProjectID  string
	DatabaseID string
	Name       string
	Prefix     string
	LastFour   string
	SecretHash string
	Scopes     []string
	CreatedAt  time.Time
	ExpiresAt  time.Time
	RevokedAt  time.Time
}

type Billing struct {
	// Internal marks TerraScale's report-only billing; spend caps never suspend it.
	Internal          bool
	CustomerID        string
	SubscriptionID    string
	TrialUsed         bool
	ChangeVersion     uint64
	PendingPlanKey    string
	PendingInterval   string
	ScheduledPlanKey  string
	ScheduledInterval string
	ScheduledAt       time.Time
	ScheduleID        string
	// CancelAtPeriodEnd mirrors Stripe: the subscription ends at renewal
	// unless it is resumed in the billing portal.
	CancelAtPeriodEnd     bool
	PaymentMethodLastFour string
	PaymentMethodExpiry   string
	PaymentMethodBrand    string
	Status                string
	PlanKey               string
	Interval              string
	CheckoutID            string
	ReturnPath            string
	PaidExcessEnabled     bool
	SpendCapCents         int64
	Invoices              []Invoice
	// SyncedAt is when the projected Stripe state was read, so a slower
	// sync cannot project older state over a newer one.
	SyncedAt time.Time
}

type CheckoutSession struct {
	ID             string
	OwnerUserID    string
	OrganizationID string
	PlanID         string
	Interval       string
	ReturnPath     string
	Status         string
	// Demo identifies a local checkout whose own expiry is authoritative.
	// External checkouts remain protected until the provider confirms a terminal state.
	Demo      bool
	CreatedAt time.Time
	ExpiresAt time.Time
}

type Invoice struct {
	ID          string
	Number      string
	Status      string
	AmountCents int64
	Currency    string
	CreatedAt   time.Time
	PeriodStart time.Time
	PeriodEnd   time.Time
	HostedURL   string
	PDFURL      string
}

type Plan struct {
	ID                 string
	Key                string
	Name               string
	PriceCents         int64
	AnnualPriceCents   int64
	TrialDays          int
	Visible            bool
	StorageMiB         int64
	MonthlyRequests    int64
	OperationsPerMonth int64
	StorageLabel       string
	MaxDatabases       int
	MaxMembers         int
	// Internal estimate rates for writes and storage; reads are free.
	WriteCentsPerMillion    int64
	StorageCentsPerGiBMonth int64
}

// Sessions keep opaque bearer values inside the implementation; callers only
// see the authenticated principal.
type Sessions interface {
	Start(http.ResponseWriter, *http.Request, Principal) error
	Principal(*http.Request) (Principal, bool)
	BearerPrincipal(*http.Request) (Principal, bool)
	// End revokes the supplied sessions and clears the cookie only on success.
	End(http.ResponseWriter, *http.Request) error
	// RevokeUser ends every session of the user in the account.
	RevokeUser(ctx context.Context, accountID, userID string) error
}

// CredentialSessions binds a password login to the credential version that
// was verified. Registration may use Sessions.Start directly.
type CredentialSessions interface {
	StartIfPasswordHash(http.ResponseWriter, *http.Request, Principal, string) error
}

// CLIGrantSessions binds device approval to a live browser session without
// exposing or retaining its bearer secret in the authorization feature.
// BindCLIGrant returns an opaque, storable binding to the request's live
// session; CLIGrantPrincipal resolves it to the session's current principal
// for as long as that session lives. Resolution errors preserve transient
// storage failures so a caller can retry instead of invalidating an approval.
type CLIGrantSessions interface {
	BindCLIGrant(*http.Request) (string, bool)
	CLIGrantPrincipal(ctx context.Context, binding string) (Principal, bool, error)
}

// Throttle bounds password guessing. Allow reserves one attempt for the
// request's client and the account before the password is checked, and
// reports how long to wait once either budget is spent. Forgive returns the
// reservation after a correct password, so only failures count.
type Throttle interface {
	ClientKey(r *http.Request) string
	ClientAddr(r *http.Request) string
	Allow(r *http.Request, account string) (time.Duration, bool)
	Forgive(r *http.Request, account string)
}

type NavItem struct {
	Label      string
	URL        string
	Active     bool
	BadgeCount int
	// BadgeLabel names the count for assistive technology, e.g. "2 unread";
	// without it the count is decorative.
	BadgeLabel string
	// Group names the run of sibling sub-navigation items this item starts
	// or continues; the sidebar renders a label where the group changes.
	Group string
	// Children render as a collapsible sub-navigation under the item.
	Children []NavItem
}

// AuthNavigation returns the authentication console sections for the sidebar
// sub-navigation, grouped the way hosted identity consoles lay them out:
// getting started, people, sign-in configuration, developer integrations,
// then customization. Each call returns a fresh slice the caller may mark
// active.
func AuthNavigation() []NavItem {
	return []NavItem{
		{Label: "Overview", URL: "/auth/overview"},
		{Label: "Quickstart", URL: "/auth/quickstart"},
		{Label: "Users", URL: "/auth/users", Group: "Manage"},
		{Label: "Organizations", URL: "/auth/organizations", Group: "Manage"},
		{Label: "Memberships", URL: "/auth/memberships", Group: "Manage"},
		{Label: "Invitations", URL: "/auth/invitations", Group: "Manage"},
		{Label: "Enrollment domains", URL: "/auth/domains", Group: "Manage"},
		{Label: "Sign-in methods", URL: "/auth/sign-in", Group: "User & authentication"},
		{Label: "SSO connections", URL: "/auth/connections", Group: "User & authentication"},
		{Label: "Multi-factor", URL: "/auth/mfa", Group: "User & authentication"},
		{Label: "Sessions", URL: "/auth/sessions", Group: "User & authentication"},
		{Label: "Directory sync", URL: "/auth/directories", Group: "User & authentication"},
		{Label: "OAuth applications", URL: "/auth/clients", Group: "Developers"},
		{Label: "JWT templates", URL: "/auth/templates", Group: "Developers"},
		{Label: "Management keys", URL: "/auth/keys", Group: "Developers"},
		{Label: "Webhooks", URL: "/auth/webhooks", Group: "Developers"},
		{Label: "Audit log", URL: "/auth/audit", Group: "Developers"},
		{Label: "Branding", URL: "/auth/branding", Group: "Customization"},
		{Label: "Emails", URL: "/auth/emails", Group: "Customization"},
	}
}

type NavGroup struct {
	Label string
	Items []NavItem
}

type Shell struct {
	UserName         string
	UserInitials     string
	UserEmail        string
	OrganizationName string
	ProjectName      string
	Organizations    []OrganizationOption
	Projects         []ProjectOption
	Navigation       []NavGroup
	// ReturnURL is where switching organization or project lands.
	ReturnURL string
	// Notices are the status page's active incidents and maintenance.
	Notices []StatusNotice
	// NoticeScope separates dismissed banners by browser login. It is not a credential.
	NoticeScope string
	// UnreadNotifications counts unread notifications in the selected scope.
	UnreadNotifications int
}

// StatusNotice is one unresolved incident or current or upcoming maintenance
// window from the public status page, shown as a banner in the shell.
type StatusNotice struct {
	ID string
	// Impact is none, minor, major, critical, or maintenance.
	Impact  string
	Label   string
	Title   string
	Message string
	URL     string
}

type OrganizationOption struct {
	ID       string
	Name     string
	Selected bool
}

type ProjectOption struct {
	ID       string
	Name     string
	Selected bool
}

type CSRF interface {
	Token(http.ResponseWriter, *http.Request) string
	Verify(*http.Request) error
	Protect(http.Handler) http.Handler
}

type Page struct {
	Layout string
	Title  string
	// Status is the response status code; zero means 200 OK. Forms that fail
	// validation render their page again with a 4xx status.
	Status    int
	Principal Principal
	View      View
	CSRFToken string
	Shell     Shell
	Nonce     string
	DevMode   bool
}

// View constructs a feature component after the renderer has populated the
// request's CSRF token and, for full pages, the nonce and canonical shell.
// Feature packages close over their own typed page data in this callback.
type View func(Page) templ.Component

// Renderer renders feature components inside the shared document layouts.
type Renderer interface {
	Page(http.ResponseWriter, *http.Request, Page) error
}

// Events streams Datastar patches to the browser.
type Events interface {
	PatchElements(http.ResponseWriter, *http.Request, string) error
}

// SDKFactory returns ts-go clients of a region's TerraBase (Region.API).
// Features check the principal against the project in Store first.
type SDKFactory interface {
	// Documents is the Documents API with a data API key.
	Documents(api, key string) (*v1.Client, error)
	Management(api string) (*v1.Management, error)
}

// Deps is shared across independent feature packages. The implementation of
// each interface lives in platform packages; feature packages import core only.
type Deps struct {
	Store    Store
	Sessions Sessions
	CSRF     CSRF
	Render   Renderer
	Events   Events
	// SDK and Management reach TerraBase, where every database outside the
	// demo lives (Data); both are nil in the demo. Management acts as the
	// signed-in user, and also reads the system databases for platform
	// admins (System).
	SDK        SDKFactory
	Management *v1.Management
	// Usage reads TerraBase's metered usage; nil without TerraBase.
	Usage    UsageMeter
	Throttle Throttle
	// SignUpMail limits verification emails to one per minute per address.
	SignUpMail Throttle
	Tools      *Tools
	// Regions lists the regions a database can be placed in, from ts-atlas
	// discovery; nil when discovery is not configured.
	Regions func(context.Context) ([]Region, error)
	Demo    bool
	// DocumentsOnly is TerraBase without ts-auth, a test setup: with no
	// user to act as in TerraBase, databases keep their contents in the
	// dashboard's records, as in phase 1 (TS-472). Production signs in
	// through ts-auth (config.Load).
	DocumentsOnly bool
}

// Region is a region ts-atlas discovery lists. Code is the dashboard's
// region code, such as GRU; ID is the Atlas region id TerraBase places
// databases in, such as gru-1; API is the region's TerraBase origin.
type Region struct {
	Code, ID, Name, Country, API string
	Healthy                      bool
}

// LocalResources reports that databases keep their contents in the
// dashboard's own records: in the in-memory demo, and in DocumentsOnly.
func (d Deps) LocalResources() bool { return d.Demo || d.DocumentsOnly }

func NormalizeEmail(email string) string {
	return strings.ToLower(strings.TrimSpace(email))
}

// Principal returns the account's principal for its saved dashboard
// selection.
func (a Account) Principal() Principal {
	return a.scopedPrincipal(a.ActiveOrganizationID, a.ActiveProjectID)
}

func (a Account) scopedPrincipal(organizationID, projectID string) Principal {
	p := Principal{
		UserID: a.User.ID, AccountID: a.ID, OrganizationID: organizationID,
		ProjectID: projectID, Role: a.User.Role,
		PlatformAdmin: a.User.PlatformAdmin, AuthVersion: a.AuthVersion,
	}
	if org, ok := a.Organizations[p.OrganizationID]; ok {
		if member, ok := org.Members[p.UserID]; ok {
			p.Role = member.Role
		}
	}
	return p
}

func (a Account) OrganizationFor(p Principal, id string) (Organization, error) {
	if a.ID != p.AccountID || a.User.ID != p.UserID {
		return Organization{}, ErrForbidden
	}
	org, ok := a.Organizations[id]
	if !ok {
		return Organization{}, ErrNotFound
	}
	member, ok := org.Members[p.UserID]
	if !ok && org.OwnerUserID != p.UserID {
		return Organization{}, ErrForbidden
	}
	if ok && member.Status != "" && !strings.EqualFold(member.Status, "active") {
		return Organization{}, ErrForbidden
	}
	return org, nil
}

func (a Account) ProjectFor(p Principal, organizationID, projectID string) (Project, error) {
	org, err := a.OrganizationFor(p, organizationID)
	if err != nil {
		return Project{}, err
	}
	project, ok := org.Projects[projectID]
	if !ok || project.OrganizationID != org.ID {
		return Project{}, ErrNotFound
	}
	return project, nil
}

// DatabaseCount returns how many databases the organization's projects hold.
func (o Organization) DatabaseCount() int {
	count := 0
	for _, project := range o.Projects {
		count += len(project.Databases)
	}
	return count
}

// DefaultProjectID returns the organization's default project, or the first
// project by ID when none is marked default, or "" when it has no projects.
// Projects keyed under another organization are skipped, as ProjectFor
// refuses them.
func (o Organization) DefaultProjectID() string {
	ids := make([]string, 0, len(o.Projects))
	for id, project := range o.Projects {
		if project.OrganizationID != o.ID {
			continue
		}
		if project.IsDefault {
			return id
		}
		ids = append(ids, id)
	}
	if len(ids) == 0 {
		return ""
	}
	return slices.Min(ids)
}

// RemoveDatabase deletes a database with everything scoped to it, so no
// project record outlives the database it points at: the API keys limited to
// it are revoked at now, and its shared result configurations and backup
// policy are deleted.
func (p *Project) RemoveDatabase(id string, now time.Time) {
	delete(p.Databases, id)
	delete(p.BackupPolicies, id)
	maps.DeleteFunc(p.SharedResultConfigs, func(_ string, config SharedResultConfig) bool {
		return config.DatabaseID == id
	})
	for keyID, key := range p.APIKeys {
		if key.DatabaseID == id && key.RevokedAt.IsZero() {
			key.RevokedAt = now
			p.APIKeys[keyID] = key
		}
	}
}

func (o Organization) CanProvisionResources(userID string) bool {
	if o.Archived || o.DeletionRequested || !strings.EqualFold(o.Status, "active") {
		return false
	}
	if !o.CanManage(userID) {
		return false
	}
	if strings.EqualFold(o.Kind, "personal") {
		return o.OwnerUserID == userID
	}
	return strings.EqualFold(o.BillingStatus, "active")
}

func (o Organization) CanManage(userID string) bool {
	m, ok := o.Members[userID]
	if !ok || (m.Status != "" && !strings.EqualFold(m.Status, "active")) {
		return false
	}
	return strings.EqualFold(m.Role, "owner") || strings.EqualFold(m.Role, "admin")
}

func (o Organization) CanWrite(userID string) bool {
	m, ok := o.Members[userID]
	if !ok || (m.Status != "" && !strings.EqualFold(m.Status, "active")) {
		return false
	}
	return o.CanManage(userID) || strings.EqualFold(m.Role, "member")
}

// RecordAudit appends a low-risk dashboard audit event for an action the
// principal took in its selected scope and returns it.
func (a *Account) RecordAudit(p Principal, action, target, targetType string, at time.Time) AuditEvent {
	event := AuditEvent{
		ID: NewID("evt"), OrganizationID: p.OrganizationID, ProjectID: p.ProjectID,
		Actor: a.User.Email, Action: action, Target: target, TargetType: targetType,
		Risk: "low", Source: "dashboard", OccurredAt: at,
	}
	a.AuditEvents = append(a.AuditEvents, event)
	return event
}
