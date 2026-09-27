package access

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"net/url"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/organization"
)

var ErrInvalidInvitationAction = core.Invalid("action must be accept or decline")

// Service owns the team views and invitations of the principal's
// organization. Member and invitation changes delegate to the organization
// service so HTTP routes and MCP tools share one implementation.
type Service struct {
	store         *core.Store
	organizations organization.Service
}

func NewService(store *core.Store, organizations organization.Service) Service {
	return Service{store: store, organizations: organizations}
}

// Members lists the organization's active members matching search (name or
// email) and role ("", "all", owner, member, or viewer), with the role counts
// of every active member.
func (s Service) Members(p core.Principal, search, role string) (memberList, error) {
	if role != "" && role != "all" && role != "owner" && role != "member" && role != "viewer" {
		return memberList{}, core.Invalid("role must be owner, member, or viewer")
	}
	_, org, err := s.organizations.Snapshot(p)
	if err != nil {
		return memberList{}, err
	}
	list := memberList{Members: memberViews(org, p, search, role)}
	for _, member := range memberViews(org, p, "", "") {
		switch {
		case member.IsOwner:
			list.RoleCounts.Owner++
		case member.Role == "member":
			list.RoleCounts.Member++
		case member.Role == "viewer":
			list.RoleCounts.Viewer++
		}
	}
	return list, nil
}

// Invitations lists every invitation of the organization, pending first.
func (s Service) Invitations(p core.Principal) ([]invitationView, error) {
	_, org, err := s.organizations.Snapshot(p)
	if err != nil {
		return nil, err
	}
	return invitationViews(org), nil
}

// Invite creates an invitation and returns it with its one-time link path.
func (s Service) Invite(p core.Principal, email, role string) (invitationView, string, error) {
	if role == "" {
		role = "member"
	}
	invitation, token, err := s.organizations.CreateInvitation(p, email, role)
	if err != nil {
		return invitationView{}, "", err
	}
	return invitationViewOf(invitation), inviteURL(token), nil
}

// Resend replaces a pending invitation's link and extends its expiry.
func (s Service) Resend(p core.Principal, invitationID string) (invitationView, string, error) {
	invitation, token, err := s.organizations.ResendInvitation(p, invitationID)
	if err != nil {
		return invitationView{}, "", err
	}
	return invitationViewOf(invitation), inviteURL(token), nil
}

// Invitation previews the pending invitation a token or link names for the
// principal.
func (s Service) Invitation(p core.Principal, token string) (invitationPreview, error) {
	org, invitation, ok := s.store.FindInvitation(hashInvitationToken(invitationToken(token)))
	if !ok {
		return invitationPreview{}, fmt.Errorf("%w: no pending invitation matches that token", core.ErrNotFound)
	}
	account, err := s.store.SnapshotAs(p)
	if err != nil {
		return invitationPreview{}, err
	}
	return invitationPreview{
		OrganizationID: org.ID, OrganizationName: org.Name, Email: invitation.Email, Role: invitation.Role,
		ExpiresAt:  invitation.ExpiresAt,
		CanRespond: core.NormalizeEmail(account.User.Email) == core.NormalizeEmail(invitation.Email),
	}, nil
}

// RespondToInvitation accepts or declines the invitation a token or link
// names. Accepting selects the joined organization.
func (s Service) RespondToInvitation(p core.Principal, token, action string) (core.Organization, error) {
	token = invitationToken(token)
	if token == "" {
		return core.Organization{}, core.ErrNotFound
	}
	hash := hashInvitationToken(token)
	switch strings.ToLower(strings.TrimSpace(action)) {
	case "accept":
		return s.store.AcceptInvitation(p, hash)
	case "decline":
		return s.store.DeclineInvitation(p, hash)
	default:
		return core.Organization{}, ErrInvalidInvitationAction
	}
}

func memberViews(org core.Organization, p core.Principal, search, role string) []memberView {
	search = strings.ToLower(strings.TrimSpace(search))
	members := make([]memberView, 0, len(org.Members))
	for _, member := range org.Members {
		if member.Status != "active" {
			continue
		}
		if role != "" && role != "all" && member.Role != role {
			continue
		}
		if search != "" && !strings.Contains(strings.ToLower(member.Name), search) && !strings.Contains(strings.ToLower(member.Email), search) {
			continue
		}
		members = append(members, memberView{
			ID: member.UserID, Name: member.Name, Email: member.Email, Role: member.Role,
			IsOwner:  member.UserID == org.OwnerUserID || member.Role == "owner",
			IsSelf:   member.UserID == p.UserID,
			CanEdit:  org.OwnerUserID == p.UserID && member.UserID != p.UserID && member.UserID != org.OwnerUserID && member.Role != "owner",
			JoinedAt: member.CreatedAt.UTC().Format("2006-01-02"),
		})
	}
	slices.SortFunc(members, func(a, b memberView) int { return strings.Compare(a.Name, b.Name) })
	return members
}

func invitationViews(org core.Organization) []invitationView {
	invitations := make([]invitationView, 0, len(org.Invitations))
	for _, invitation := range org.Invitations {
		if invitation.Email != "" {
			invitations = append(invitations, invitationViewOf(invitation))
		}
	}
	slices.SortFunc(invitations, func(a, b invitationView) int {
		aPending, bPending := a.Status == "Pending", b.Status == "Pending"
		if aPending && !bPending {
			return -1
		}
		if !aPending && bPending {
			return 1
		}
		return strings.Compare(a.Email, b.Email)
	})
	return invitations
}

func invitationViewOf(invitation core.Invitation) invitationView {
	return invitationView{
		ID: invitation.ID, Email: invitation.Email, Role: invitation.Role,
		Status:    invitationStatusLabel(invitation.Status),
		ExpiresAt: invitation.ExpiresAt.UTC().Format(time.RFC3339),
	}
}

func inviteURL(token string) string {
	return "/invitations/" + url.PathEscape(token)
}

// invitationToken accepts a bare token or an invitation link and returns the
// token.
func invitationToken(raw string) string {
	raw = strings.TrimSpace(raw)
	if _, after, found := strings.Cut(raw, "/invitations/"); found {
		raw, _, _ = strings.Cut(after, "/")
		raw, _, _ = strings.Cut(raw, "?")
		if unescaped, err := url.PathUnescape(raw); err == nil {
			raw = unescaped
		}
	}
	return raw
}

func hashInvitationToken(token string) string {
	digest := sha256.Sum256([]byte(token))
	return hex.EncodeToString(digest[:])
}
