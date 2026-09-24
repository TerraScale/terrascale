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
)

// Service contains organization and project behavior. Persistence and atomic
// account scoping remain owned by core.Store.
type Service struct {
	store *core.Store
	now   func() time.Time
}

func NewService(store *core.Store) Service {
	return Service{store: store, now: func() time.Time { return time.Now().UTC() }}
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
	if name == "" || len([]rune(name)) > 128 || slug(name) == "" {
		return core.Organization{}, core.ErrInvalid
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
				return core.ErrConflict
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
		return core.ErrInvalid
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
		a.ActiveProjectID = defaultProjectID(org)
		return nil
	})
}

func (s Service) RenameOrganization(p core.Principal, name string) error {
	name = strings.TrimSpace(name)
	if name == "" || len([]rune(name)) > 128 || slug(name) == "" {
		return core.ErrInvalid
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

func (s Service) CreateProject(p core.Principal, name, region, backupRegion string, regions []string) (core.Project, error) {
	name = strings.TrimSpace(name)
	if name == "" || len([]rune(name)) > 128 || slug(name) == "" {
		return core.Project{}, core.ErrInvalid
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
				return core.ErrConflict
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
	if projectID == "" || name == "" || len([]rune(name)) > 128 || slug(name) == "" {
		return core.ErrInvalid
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
				return core.ErrConflict
			}
		}
		org.Projects[projectID] = project
		a.Organizations[org.ID] = org
		return nil
	})
}

func (s Service) SwitchProject(p core.Principal, projectID string) error {
	if projectID == "" {
		return core.ErrInvalid
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := a.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		project, ok := org.Projects[projectID]
		if !ok || project.OrganizationID != org.ID {
			return core.ErrNotFound
		}
		a.ActiveOrganizationID = org.ID
		a.ActiveProjectID = projectID
		return nil
	})
}

func (s Service) UpdateMemberRole(p core.Principal, userID, role string) error {
	role = strings.ToLower(strings.TrimSpace(role))
	if userID == "" || (role != roleMember && role != roleViewer) || userID == p.UserID {
		return core.ErrInvalid
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		member, ok := org.Members[userID]
		if !ok || member.Status != statusActive || userID == org.OwnerUserID || member.Role == roleOwner {
			return core.ErrNotFound
		}
		member.Role = role
		org.Members[userID] = member
		a.Organizations[org.ID] = org
		return nil
	})
}

func (s Service) RemoveMember(p core.Principal, userID string) error {
	if userID == "" || userID == p.UserID {
		return core.ErrInvalid
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		member, ok := org.Members[userID]
		if !ok || userID == org.OwnerUserID || member.Role == roleOwner {
			return core.ErrNotFound
		}
		delete(org.Members, userID)
		a.Organizations[org.ID] = org
		return nil
	})
}

func (s Service) CreateInvitation(p core.Principal, email, role string) (core.Invitation, string, error) {
	email = core.NormalizeEmail(email)
	role = strings.ToLower(strings.TrimSpace(role))
	parsed, err := mail.ParseAddress(email)
	if err != nil || !strings.EqualFold(parsed.Address, email) || (role != roleMember && role != roleViewer) {
		return core.Invitation{}, "", core.ErrInvalid
	}
	token, err := newToken()
	if err != nil {
		return core.Invitation{}, "", err
	}
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
		for _, member := range org.Members {
			if core.NormalizeEmail(member.Email) == email && member.Status == statusActive {
				return core.ErrConflict
			}
		}
		for _, existing := range org.Invitations {
			if core.NormalizeEmail(existing.Email) == email && existing.Status == statusPending && existing.ExpiresAt.After(s.now()) {
				return core.ErrConflict
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
		return core.Invitation{}, "", core.ErrInvalid
	}
	token, err := newToken()
	if err != nil {
		return core.Invitation{}, "", err
	}
	digest := sha256.Sum256([]byte(token))
	var invitation core.Invitation
	err = s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		current, ok := org.Invitations[invitationID]
		if !ok || current.Status != statusPending {
			return core.ErrNotFound
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
			return core.ErrNotFound
		}
		invitation.Status = "revoked"
		org.Invitations[invitationID] = invitation
		a.Organizations[org.ID] = org
		return nil
	})
}

func (s Service) RequestOwnershipTransfer(p core.Principal, targetUserID string) error {
	if targetUserID == "" || targetUserID == p.UserID {
		return core.ErrInvalid
	}
	return s.store.UpdateAs(p, func(a *core.Account) error {
		org, err := editableOrganization(a, p)
		if err != nil {
			return err
		}
		target, ok := org.Members[targetUserID]
		if !ok || target.Status != statusActive || target.Role == roleOwner {
			return core.ErrNotFound
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
			return core.ErrForbidden
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
			return core.ErrInvalid
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
			return core.ErrConflict
		}
		delete(org.Members, p.UserID)
		a.Organizations[org.ID] = org
		fallbackID, fallback := accessibleOrganization(a, p.UserID)
		if fallbackID == "" {
			return core.ErrConflict
		}
		a.ActiveOrganizationID = fallbackID
		a.ActiveProjectID = defaultProjectID(fallback)
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

func defaultProjectID(org core.Organization) string {
	ids := make([]string, 0, len(org.Projects))
	for id, project := range org.Projects {
		if project.OrganizationID == org.ID && project.IsDefault {
			return id
		}
		ids = append(ids, id)
	}
	slices.Sort(ids)
	if len(ids) == 0 {
		return ""
	}
	return ids[0]
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
			return nil, core.ErrInvalid
		}
		if _, ok := seen[region]; ok {
			continue
		}
		seen[region] = struct{}{}
		result = append(result, region)
	}
	if len(result) == 0 {
		return nil, core.ErrInvalid
	}
	return result, nil
}

func parseRegion(value string) (string, error) {
	region, err := sdk.ParseRegionCode(value)
	if err != nil {
		return "", core.ErrInvalid
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

func newToken() (string, error) {
	var raw [32]byte
	if _, err := rand.Read(raw[:]); err != nil {
		return "", fmt.Errorf("create invitation token: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(raw[:]), nil
}
