package organization

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"net/mail"
	"slices"
	"strings"
	"time"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

const (
	kindPersonal  = "personal"
	kindBusiness  = "business"
	statusActive  = "active"
	statusPending = "pending"
	roleOwner     = "owner"
	roleMember    = "member"
	roleViewer    = "viewer"
)

var (
	ErrRequiresOwner = errors.New("organization owner role required")
	ErrBillingNeeded = errors.New("active billing is required to create projects")
	ErrPersonalOrg   = errors.New("personal organizations cannot be deleted or left")
	ErrNoTransfer    = errors.New("no ownership transfer is pending for this member")
	ErrMemberLimit   = fmt.Errorf("%w: the plan's member limit is reached", core.ErrConflict)
)

var (
	errNameRequired = core.Invalid("names need 1-128 characters including a letter or digit")
	errMemberRole   = core.Invalid("role must be member or viewer")
)

// Service contains organization and project behavior. HTTP handlers and MCP
// tools both call it. Persistence and atomic account scoping remain owned by
// core.Store.
type Service struct {
	store    *core.Store
	sessions core.Sessions
	now      func() time.Time
}

func NewService(store *core.Store, sessions core.Sessions) Service {
	return Service{store: store, sessions: sessions, now: func() time.Time { return time.Now().UTC() }}
}

func (s Service) Snapshot(p core.Principal) (core.Account, core.Organization, error) {
	a, err := s.store.SnapshotAs(p)
	if err != nil {
		return core.Account{}, core.Organization{}, err
	}
	org, err := a.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		return core.Account{}, core.Organization{}, err
	}
	return a, org, nil
}

// ForOrganization derives a request principal for another tenant only after
// confirming that the same signed-in account is a member. Store.UpdateAs
// checks that membership again under its mutation lock, so wizard form fields
// cannot grant cross-organization access.
func (s Service) ForOrganization(p core.Principal, organizationID string) (core.Principal, error) {
	a, err := s.store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, err
	}
	org, err := a.OrganizationFor(p, organizationID)
	if err != nil {
		return core.Principal{}, err
	}
	p.OrganizationID = org.ID
	p.ProjectID = ""
	p.Role = roleFor(org, p.UserID)
	return p, nil
}

// CreateBusiness creates or resumes the caller's same-name pending tenant and
// selects it in the account. Checkout and plan changes are owned by billing.
func (s Service) CreateBusiness(p core.Principal, name string) (core.Organization, error) {
	name = strings.TrimSpace(name)
	if name == "" || utf8.RuneCountInString(name) > 128 || slug(name) == "" {
		return core.Organization{}, errNameRequired
	}
	var result core.Organization
	err := s.store.UpdateAs(p, func(a *core.Account) error {
		for id, org := range a.Organizations {
			if org.Kind == kindBusiness && org.Status == statusActive && org.BillingStatus == "billing_required" && org.OwnerUserID == p.UserID && strings.EqualFold(org.Name, name) {
				a.ActiveOrganizationID = id
				a.ActiveProjectID = ""
				result = org
				return nil
			}
		}
		for _, org := range a.Organizations {
			if org.OwnerUserID == p.UserID && org.Slug == slug(name) {
				return fmt.Errorf("%w: you already own an organization named %q", core.ErrConflict, org.Name)
			}
		}

		now := s.now()
		id := core.NewID("org")
		result = core.Organization{
			ID: id, Name: name, Kind: kindBusiness, Slug: slug(name), Status: statusActive,
			OwnerUserID: p.UserID, PlanID: "plan_starter", BillingStatus: "billing_required",
			Projects: make(map[string]core.Project),
			Members: map[string]core.Member{
				p.UserID: {UserID: p.UserID, Email: a.User.Email, Name: a.User.Name, Role: roleOwner, Status: statusActive, CreatedAt: now},
			},
			Invitations: make(map[string]core.Invitation), CheckoutSessions: make(map[string]core.CheckoutSession), CreatedAt: now,
		}
		a.Organizations[id] = result
		a.ActiveOrganizationID = id
		a.ActiveProjectID = ""
		return nil
	})
	return result, err
}

func (s Service) SwitchOrganization(p core.Principal, organizationID string) error {
	if organizationID == "" {
		return core.Invalid("organizationId is required")
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := a.OrganizationFor(p, organizationID)
		if err != nil {
			return err
		}
		if !strings.EqualFold(org.Status, statusActive) {
			return core.ErrForbidden
		}
		a.ActiveOrganizationID = organizationID
		a.ActiveProjectID = org.DefaultProjectID()
		return nil
	})
}

func (s Service) RenameOrganization(p core.Principal, name string) error {
	name = strings.TrimSpace(name)
	if name == "" || utf8.RuneCountInString(name) > 128 || slug(name) == "" {
		return errNameRequired
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		org.Name = name
		org.Slug = slug(name)
		a.Organizations[org.ID] = org
		return nil
	})
}

// CreateProject adds a project to the principal's organization. Regions
// default to the primary region when none are listed.
func (s Service) CreateProject(p core.Principal, name, region, backupRegion string, regions []string) (core.Project, error) {
	name = strings.TrimSpace(name)
	if name == "" || utf8.RuneCountInString(name) > 128 || slug(name) == "" {
		return core.Project{}, errNameRequired
	}
	if len(regions) == 0 && region != "" {
		regions = []string{region}
	}
	validatedRegions, err := normalizeRegions(regions)
	if err != nil {
		return core.Project{}, err
	}
	primary, err := parseRegion(region)
	if err != nil {
		primary = validatedRegions[0]
	}
	if !slices.Contains(validatedRegions, primary) {
		primary = validatedRegions[0]
	}
	backup, err := parseRegion(backupRegion)
	if err != nil || !slices.Contains(validatedRegions, backup) {
		backup = validatedRegions[0]
	}
	var result core.Project
	err = s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		if !canProvision(org, p.UserID) {
			return ErrBillingNeeded
		}
		projectSlug := slug(name)
		for _, existing := range org.Projects {
			if strings.EqualFold(existing.Slug, projectSlug) {
				return fmt.Errorf("%w: project %q already exists", core.ErrConflict, existing.Name)
			}
		}
		now := s.now()
		result = core.Project{
			ID: core.NewID("prj"), OrganizationID: org.ID, Name: name, Region: primary,
			BackupRegion: backup, Regions: validatedRegions, Slug: projectSlug, Status: statusActive,
			Databases: make(map[string]core.Database), APIKeys: make(map[string]core.APIKey),
			SharedResultConfigs: make(map[string]core.SharedResultConfig), CreatedAt: now,
		}
		org.Projects[result.ID] = result
		a.Organizations[org.ID] = org
		if a.ActiveOrganizationID == org.ID && a.ActiveProjectID == "" {
			a.ActiveProjectID = result.ID
		}
		return nil
	})
	return result, err
}

func (s Service) RenameProject(p core.Principal, projectID, name, region, backupRegion string, regions []string) error {
	name = strings.TrimSpace(name)
	if projectID == "" {
		return core.Invalid("projectId is required")
	}
	if name == "" || utf8.RuneCountInString(name) > 128 || slug(name) == "" {
		return errNameRequired
	}
	var validatedRegions []string
	if len(regions) > 0 {
		var err error
		validatedRegions, err = normalizeRegions(regions)
		if err != nil {
			return err
		}
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		project, ok := org.Projects[projectID]
		if !ok || project.OrganizationID != org.ID {
			return core.ErrNotFound
		}
		project.Name = name
		project.Slug = slug(name)
		if len(validatedRegions) > 0 {
			project.Regions = validatedRegions
			if parsed, parseErr := parseRegion(region); parseErr == nil && slices.Contains(validatedRegions, parsed) {
				project.Region = parsed
			} else {
				project.Region = validatedRegions[0]
			}
			if parsed, parseErr := parseRegion(backupRegion); parseErr == nil && slices.Contains(validatedRegions, parsed) {
				project.BackupRegion = parsed
			} else {
				project.BackupRegion = validatedRegions[0]
			}
		}
		for id, existing := range org.Projects {
			if id != projectID && strings.EqualFold(existing.Slug, project.Slug) {
				return fmt.Errorf("%w: project %q already exists", core.ErrConflict, existing.Name)
			}
		}
		org.Projects[projectID] = project
		a.Organizations[org.ID] = org
		return nil
	})
}

// SwitchProject selects a project in any organization the principal belongs
// to, together with that organization.
func (s Service) SwitchProject(p core.Principal, projectID string) error {
	if projectID == "" {
		return core.Invalid("projectId is required")
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		for id, candidate := range a.Organizations {
			project, ok := candidate.Projects[projectID]
			if !ok || project.OrganizationID != id {
				continue
			}
			org, err := a.OrganizationFor(p, id)
			if err != nil {
				return err
			}
			if !strings.EqualFold(org.Status, statusActive) {
				return core.ErrForbidden
			}
			a.ActiveOrganizationID = org.ID
			a.ActiveProjectID = projectID
			return nil
		}
		return fmt.Errorf("%w: project %q", core.ErrNotFound, projectID)
	})
}

func (s Service) UpdateMemberRole(p core.Principal, userID, role string) error {
	role = strings.ToLower(strings.TrimSpace(role))
	if role != roleMember && role != roleViewer {
		return errMemberRole
	}
	if userID == "" || userID == p.UserID {
		return core.Invalid("choose another member than yourself")
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		member, ok := org.Members[userID]
		if !ok || member.Status != statusActive || userID == org.OwnerUserID || member.Role == roleOwner {
			return fmt.Errorf("%w: member %q", core.ErrNotFound, userID)
		}
		member.Role = role
		org.Members[userID] = member
		a.Organizations[org.ID] = org
		return nil
	})
}

// RemoveMember removes a member from the principal's organization and ends
// the removed user's dashboard sessions.
func (s Service) RemoveMember(p core.Principal, userID string) error {
	if userID == "" || userID == p.UserID {
		return core.Invalid("choose another member than yourself")
	}
	var removed core.Member
	err := s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		member, ok := org.Members[userID]
		if !ok || userID == org.OwnerUserID || member.Role == roleOwner {
			return fmt.Errorf("%w: member %q", core.ErrNotFound, userID)
		}
		removed = member
		delete(org.Members, userID)
		a.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		return err
	}
	if target, found := s.store.FindByEmail(removed.Email); found && s.sessions != nil {
		s.sessions.RevokeUser(target.ID, userID)
	}
	return nil
}

func (s Service) CreateInvitation(p core.Principal, email, role string) (core.Invitation, string, error) {
	email = core.NormalizeEmail(email)
	role = strings.ToLower(strings.TrimSpace(role))
	parsed, err := mail.ParseAddress(email)
	if err != nil || !strings.EqualFold(parsed.Address, email) {
		return core.Invitation{}, "", core.Invalid("enter a valid email address")
	}
	if role != roleMember && role != roleViewer {
		return core.Invitation{}, "", errMemberRole
	}
	account, err := s.store.SnapshotAs(p)
	if err != nil {
		return core.Invitation{}, "", err
	}
	selected, ok := account.Organizations[p.OrganizationID]
	if !ok {
		return core.Invitation{}, "", core.ErrNotFound
	}
	plan, ok := s.store.Plan(selected.PlanID)
	if !ok {
		return core.Invitation{}, "", core.ErrNotFound
	}
	token := newToken()
	digest := sha256.Sum256([]byte(token))
	invitation := core.Invitation{
		ID: core.NewID("inv"), Email: email, Role: role, Status: statusPending,
		TokenHash: hex.EncodeToString(digest[:]), ExpiresAt: s.now().Add(7 * 24 * time.Hour), CreatedAt: s.now(),
	}
	err = s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		if org.PlanID != plan.ID {
			return core.ErrConflict
		}
		for _, member := range org.Members {
			if core.NormalizeEmail(member.Email) == email && member.Status == statusActive {
				return fmt.Errorf("%w: %s is already a member", core.ErrConflict, email)
			}
		}
		for _, existing := range org.Invitations {
			if core.NormalizeEmail(existing.Email) == email && existing.Status == statusPending && existing.ExpiresAt.After(s.now()) {
				return fmt.Errorf("%w: %s already has a pending invitation", core.ErrConflict, email)
			}
		}
		if plan.MaxMembers > 0 {
			occupied := 0
			for _, member := range org.Members {
				if member.Status == statusActive {
					occupied++
				}
			}
			for _, existing := range org.Invitations {
				if existing.Status == statusPending && existing.ExpiresAt.After(s.now()) {
					occupied++
				}
			}
			if occupied >= plan.MaxMembers {
				return fmt.Errorf("%w: the %s plan allows %d members including pending invitations", ErrMemberLimit, plan.Name, plan.MaxMembers)
			}
		}
		if org.Invitations == nil {
			org.Invitations = make(map[string]core.Invitation)
		}
		invitation.OrganizationID = org.ID
		invitation.InviterEmail = a.User.Email
		org.Invitations[invitation.ID] = invitation
		a.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		return core.Invitation{}, "", err
	}
	return invitation, token, nil
}

func (s Service) ResendInvitation(p core.Principal, invitationID string) (core.Invitation, string, error) {
	if invitationID == "" {
		return core.Invitation{}, "", core.Invalid("invitationId is required")
	}
	token := newToken()
	digest := sha256.Sum256([]byte(token))
	var invitation core.Invitation
	err := s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		current, ok := org.Invitations[invitationID]
		if !ok || current.Status != statusPending {
			return fmt.Errorf("%w: pending invitation %q", core.ErrNotFound, invitationID)
		}
		current.TokenHash = hex.EncodeToString(digest[:])
		current.CreatedAt = s.now()
		current.ExpiresAt = s.now().Add(7 * 24 * time.Hour)
		org.Invitations[invitationID] = current
		a.Organizations[org.ID] = org
		invitation = current
		return nil
	})
	return invitation, token, err
}

func (s Service) RevokeInvitation(p core.Principal, invitationID string) error {
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		invitation, ok := org.Invitations[invitationID]
		if !ok || invitation.Status != statusPending {
			return fmt.Errorf("%w: pending invitation %q", core.ErrNotFound, invitationID)
		}
		invitation.Status = "revoked"
		org.Invitations[invitationID] = invitation
		a.Organizations[org.ID] = org
		return nil
	})
}

func (s Service) RequestOwnershipTransfer(p core.Principal, targetUserID string) error {
	if targetUserID == "" || targetUserID == p.UserID {
		return core.Invalid("choose another active member as the new owner")
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		target, ok := org.Members[targetUserID]
		if !ok || target.Status != statusActive || target.Role == roleOwner {
			return fmt.Errorf("%w: member %q", core.ErrNotFound, targetUserID)
		}
		org.PendingOwnerUserID = targetUserID
		org.PendingOwnerExpiresAt = s.now().Add(7 * 24 * time.Hour)
		a.Organizations[org.ID] = org
		return nil
	})
}

// ConfirmOwnershipTransfer requires the nominated active member to confirm.
// The owner and target roles change together, so this operation cannot leave a
// live organization without an owner.
func (s Service) ConfirmOwnershipTransfer(p core.Principal) error {
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := a.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if org.PendingOwnerUserID == "" || org.PendingOwnerUserID != p.UserID || !org.PendingOwnerExpiresAt.After(s.now()) {
			return ErrNoTransfer
		}
		oldOwner, ok := org.Members[org.OwnerUserID]
		if !ok || oldOwner.Role != roleOwner {
			return core.ErrConflict
		}
		newOwner, ok := org.Members[p.UserID]
		if !ok || newOwner.Status != statusActive {
			return core.ErrConflict
		}
		oldOwner.Role = roleMember
		newOwner.Role = roleOwner
		org.Members[oldOwner.UserID] = oldOwner
		org.Members[newOwner.UserID] = newOwner
		org.OwnerUserID = newOwner.UserID
		org.PendingOwnerUserID = ""
		org.PendingOwnerExpiresAt = time.Time{}
		a.Organizations[org.ID] = org
		return nil
	})
}

// CancelOwnershipTransfer withdraws a pending transfer: the owner cancels it,
// or the nominated member declines it.
func (s Service) CancelOwnershipTransfer(p core.Principal) error {
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := a.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if org.PendingOwnerUserID == "" {
			return ErrNoTransfer
		}
		if p.UserID != org.OwnerUserID && p.UserID != org.PendingOwnerUserID {
			return ErrRequiresOwner
		}
		org.PendingOwnerUserID = ""
		org.PendingOwnerExpiresAt = time.Time{}
		a.Organizations[org.ID] = org
		return nil
	})
}

func (s Service) RequestDeletion(p core.Principal, confirmation string) error {
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		if org.Kind == kindPersonal {
			return ErrPersonalOrg
		}
		if strings.TrimSpace(confirmation) != org.Name {
			return core.Invalid("type the organization name to confirm deletion")
		}
		org.DeletionRequested = true
		a.Organizations[org.ID] = org
		return nil
	})
}

func (s Service) LeaveOrganization(p core.Principal) error {
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := a.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if org.Kind == kindPersonal {
			return ErrPersonalOrg
		}
		if org.OwnerUserID == p.UserID {
			return fmt.Errorf("%w: owners must transfer ownership before leaving", core.ErrConflict)
		}
		delete(org.Members, p.UserID)
		a.Organizations[org.ID] = org
		if a.ActiveOrganizationID != org.ID {
			return nil
		}
		fallbackID, fallback := accessibleOrganization(a, p.UserID)
		if fallbackID == "" {
			return core.ErrConflict
		}
		a.ActiveOrganizationID = fallbackID
		a.ActiveProjectID = fallback.DefaultProjectID()
		return nil
	})
}

func editableOrganization(a *core.Account, p core.Principal) (core.Organization, error) {
	org, err := a.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		return core.Organization{}, err
	}
	if !strings.EqualFold(org.Status, statusActive) {
		return core.Organization{}, core.ErrForbidden
	}
	if org.OwnerUserID != p.UserID {
		return core.Organization{}, ErrRequiresOwner
	}
	return org, nil
}

func roleFor(org core.Organization, userID string) string {
	if org.OwnerUserID == userID {
		return roleOwner
	}
	member, ok := org.Members[userID]
	if !ok || member.Status != statusActive {
		return ""
	}
	return member.Role
}

func canProvision(org core.Organization, userID string) bool {
	if org.Status != statusActive {
		return false
	}
	if org.Kind == kindPersonal {
		return org.OwnerUserID == userID
	}
	return org.Kind == kindBusiness && org.OwnerUserID == userID && strings.EqualFold(org.BillingStatus, "active")
}

func accessibleOrganization(a *core.Account, userID string) (string, core.Organization) {
	ids := make([]string, 0, len(a.Organizations))
	for id, org := range a.Organizations {
		member, memberOK := org.Members[userID]
		if org.OwnerUserID == userID || (memberOK && member.Status == statusActive) {
			ids = append(ids, id)
		}
	}
	slices.Sort(ids)
	for _, id := range ids {
		org := a.Organizations[id]
		if org.Kind == kindPersonal {
			return id, org
		}
	}
	if len(ids) == 0 {
		return "", core.Organization{}
	}
	return ids[0], a.Organizations[ids[0]]
}

func normalizeRegions(regions []string) ([]string, error) {
	seen := make(map[string]struct{}, len(regions))
	result := make([]string, 0, len(regions))
	for _, value := range regions {
		region, err := parseRegion(value)
		if err != nil {
			return nil, err
		}
		if _, ok := seen[region]; ok {
			continue
		}
		seen[region] = struct{}{}
		result = append(result, region)
	}
	if len(result) == 0 {
		return nil, core.Invalid("choose at least one region")
	}
	return result, nil
}

func parseRegion(value string) (string, error) {
	region, err := sdk.ParseRegionCode(value)
	if err != nil {
		return "", core.Invalid(fmt.Sprintf("unknown region %q; use GRU, SFO, or FRA", value))
	}
	return string(region), nil
}

func slug(value string) string {
	var b strings.Builder
	lastDash := false
	for _, r := range strings.ToLower(strings.TrimSpace(value)) {
		if r >= 'a' && r <= 'z' || r >= '0' && r <= '9' {
			b.WriteRune(r)
			lastDash = false
		} else if b.Len() > 0 && !lastDash {
			b.WriteByte('-')
			lastDash = true
		}
	}
	return strings.Trim(b.String(), "-")
}

func newToken() string {
	var raw [32]byte
	rand.Read(raw[:])
	return base64.RawURLEncoding.EncodeToString(raw[:])
}

// Context returns the signed-in user, the saved dashboard selection, and
// every organization and project the user can access.
func (s Service) Context(p core.Principal) (contextView, error) {
	a, err := s.store.SnapshotAs(p)
	if err != nil {
		return contextView{}, err
	}
	result := contextView{
		User:          userView{ID: a.User.ID, Name: a.User.Name, Email: a.User.Email, PlatformAdmin: a.User.PlatformAdmin},
		Selection:     selectionView{OrganizationID: a.ActiveOrganizationID, ProjectID: a.ActiveProjectID},
		Organizations: []organizationSummary{},
	}
	for _, org := range a.Organizations {
		if _, err := a.OrganizationFor(p, org.ID); err == nil {
			result.Organizations = append(result.Organizations, s.summary(org, p.UserID))
		}
	}
	slices.SortFunc(result.Organizations, func(a, b organizationSummary) int { return strings.Compare(a.Name, b.Name) })
	return result, nil
}

// Overview returns the principal's organization with its projects, member
// and invitation counts, and ownership transfer state.
func (s Service) Overview(p core.Principal) (organizationDetail, error) {
	_, org, err := s.Snapshot(p)
	if err != nil {
		return organizationDetail{}, err
	}
	detail := organizationDetail{
		organizationSummary: s.summary(org, p.UserID), Slug: org.Slug, CreatedAt: org.CreatedAt,
		CanLeave:            org.Kind != kindPersonal && org.OwnerUserID != p.UserID,
		PendingOwnerUserID:  org.PendingOwnerUserID,
		CanConfirmOwnership: org.PendingOwnerUserID == p.UserID && org.PendingOwnerExpiresAt.After(s.now()),
	}
	if org.PendingOwnerUserID != "" {
		detail.PendingOwnerExpiresAt = &org.PendingOwnerExpiresAt
	}
	for _, member := range org.Members {
		if member.Status == statusActive {
			detail.MemberCount++
		}
	}
	for _, invitation := range org.Invitations {
		if invitation.Status == statusPending && invitation.ExpiresAt.After(s.now()) {
			detail.PendingInvitations++
		}
	}
	return detail, nil
}

func (s Service) summary(org core.Organization, userID string) organizationSummary {
	summary := organizationSummary{
		ID: org.ID, Name: org.Name, Kind: org.Kind, Status: org.Status, Role: roleFor(org, userID),
		PlanID: org.PlanID, BillingStatus: org.BillingStatus, CanManage: org.OwnerUserID == userID,
		CanProvision: canProvision(org, userID), DeletionRequested: org.DeletionRequested,
		Projects: []projectSummary{},
	}
	if plan, ok := s.store.Plan(org.PlanID); ok {
		summary.PlanName = plan.Name
	}
	for _, project := range org.Projects {
		if project.OrganizationID != org.ID {
			continue
		}
		summary.Projects = append(summary.Projects, projectSummary{
			ID: project.ID, Name: project.Name, Region: project.Region, BackupRegion: project.BackupRegion,
			Regions: append([]string{}, project.Regions...), IsDefault: project.IsDefault, Databases: len(project.Databases),
		})
	}
	slices.SortFunc(summary.Projects, func(a, b projectSummary) int { return strings.Compare(a.Name, b.Name) })
	return summary
}
