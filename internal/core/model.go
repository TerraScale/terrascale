package core

import (
	"errors"
	"net/http"
	"strings"
	"time"

	"github.com/a-h/templ"
	sdk "terrascale.tech/sdk"
)

var (
	ErrNotFound         = errors.New("not found")
	ErrConflict         = errors.New("conflict")
	ErrUnauthorized     = errors.New("unauthorized")
	ErrForbidden        = errors.New("forbidden")
	ErrInvalid          = errors.New("invalid input")
	ErrAlreadyProcessed = errors.New("already processed")
)

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
	ID            string
	Email         string
	Name          string
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
}

type Database struct {
	ID                   string
	ProjectID            string
	Name                 string
	Status               string
	Region               string
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
	CreatedAt                                                          time.Time
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
	CustomerID            string
	SubscriptionID        string
	TrialUsed             bool
	ChangeVersion         uint64
	PendingPlanKey        string
	PendingInterval       string
	ScheduledPlanKey      string
	ScheduledInterval     string
	ScheduledAt           time.Time
	ScheduleID            string
	PaymentMethodID       string
	PaymentMethodLastFour string
	PaymentMethodExpiry   string
	PaymentMethodBrand    string
	Status                string
	PlanKey               string
	Interval              string
	CheckoutID            string
	LastEventAt           time.Time
	ReturnPath            string
	PaidExcessEnabled     bool
	SpendCapCents         int64
	Invoices              []Invoice
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
	LastEventAt time.Time
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
}

// Sessions keep opaque bearer values inside the implementation; callers only
// see the authenticated principal.
type Sessions interface {
	Start(http.ResponseWriter, *http.Request, Principal) error
	Principal(*http.Request) (Principal, bool)
	BearerPrincipal(*http.Request) (Principal, bool)
	End(http.ResponseWriter, *http.Request)
	RevokeUser(accountID, userID string)
}

// CredentialSessions binds a password login to the credential version that
// was verified. Registration may use Sessions.Start directly.
type CredentialSessions interface {
	StartIfPasswordHash(http.ResponseWriter, *http.Request, Principal, string) error
}

type NavItem struct {
	Label      string
	URL        string
	Active     bool
	BadgeCount int
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
}

// StatusNotice is one unresolved incident or current or upcoming maintenance
// window from the public status page, shown as a banner in the shell.
type StatusNotice struct {
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
	Layout    string
	Title     string
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

// SDKFactory returns a new official SDK client after the handler has checked
// the principal against the requested organization and project in Store.
type SDKFactory interface {
	ForScope(tenantID, projectID string) (*sdk.Client, error)
}

// Deps is shared across independent feature packages. The implementation of
// each interface lives in platform packages; feature packages import core only.
type Deps struct {
	Store    *Store
	Sessions Sessions
	CSRF     CSRF
	Render   Renderer
	Events   Events
	SDK      SDKFactory
	Demo     bool
}

func NormalizeEmail(email string) string {
	return strings.ToLower(strings.TrimSpace(email))
}

func (a Account) Principal() Principal {
	p := Principal{
		UserID: a.User.ID, AccountID: a.ID, OrganizationID: a.ActiveOrganizationID,
		ProjectID: a.ActiveProjectID, Role: a.User.Role,
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
