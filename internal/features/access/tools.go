package access

import (
	"context"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type memberList struct {
	Members    []memberView `json:"members"`
	RoleCounts roleCounts   `json:"roleCounts" jsonschema:"Active members by role across the whole organization."`
}

type roleCounts struct {
	Owner  int `json:"owner"`
	Member int `json:"member"`
	Viewer int `json:"viewer"`
}

type invitationList struct {
	Invitations []invitationView `json:"invitations"`
}

type issuedInvitation struct {
	Invitation invitationView `json:"invitation"`
	InviteURL  string         `json:"inviteUrl" jsonschema:"One-time invitation link path. Prefix it with the dashboard origin and share it with the invitee; it is not shown again."`
}

type invitationPreview struct {
	OrganizationID   string    `json:"organizationId"`
	OrganizationName string    `json:"organizationName"`
	Email            string    `json:"email" jsonschema:"The invited email address; only that account can respond."`
	Role             string    `json:"role"`
	ExpiresAt        time.Time `json:"expiresAt"`
	CanRespond       bool      `json:"canRespond" jsonschema:"Whether the caller's email matches the invitation."`
}

type invitationResponse struct {
	OrganizationID   string `json:"organizationId"`
	OrganizationName string `json:"organizationName"`
	Action           string `json:"action"`
}

type listMembersInput struct {
	core.Scope
	Search string `json:"search,omitempty" jsonschema:"Case-insensitive filter on member name or email."`
	Role   string `json:"role,omitempty" jsonschema:"Only members with this role: owner, member, or viewer."`
}

type inviteInput struct {
	core.Scope
	Email string `json:"email" jsonschema:"Email address to invite."`
	Role  string `json:"role,omitempty" jsonschema:"member (read and write data) or viewer (read only). Defaults to member."`
}

type invitationInput struct {
	core.Scope
	InvitationID string `json:"invitationId" jsonschema:"Invitation ID from list_invitations."`
}

type tokenInput struct {
	Token string `json:"token" jsonschema:"Invitation token or the full invitation link."`
}

type respondInput struct {
	tokenInput
	Action string `json:"action" jsonschema:"accept or decline."`
}

type done struct {
	OK bool `json:"ok"`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_members", Title: "List members", ReadOnly: true,
		Description: "List an organization's active members with their roles, optionally filtered by name, email, or role.",
	}, func(_ context.Context, p core.Principal, in listMembersInput) (memberList, error) {
		return service.Members(p, in.Search, in.Role)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "list_invitations", Title: "List invitations", ReadOnly: true,
		Description: "List an organization's invitations with their status and expiry, pending first.",
	}, func(_ context.Context, p core.Principal, _ core.Scope) (invitationList, error) {
		invitations, err := service.Invitations(p)
		return invitationList{Invitations: invitations}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "invite_member", Title: "Invite member",
		Description: "Invite an email address to an organization and return a one-time invitation link. Requires the owner role and plan member capacity.",
	}, func(_ context.Context, p core.Principal, in inviteInput) (issuedInvitation, error) {
		invitation, link, err := service.Invite(p, in.Email, in.Role)
		return issuedInvitation{Invitation: invitation, InviteURL: link}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "resend_invitation", Title: "Resend invitation",
		Description: "Replace a pending invitation's link and extend it by 7 days. The previous link stops working. Requires the owner role.",
	}, func(_ context.Context, p core.Principal, in invitationInput) (issuedInvitation, error) {
		invitation, link, err := service.Resend(p, in.InvitationID)
		return issuedInvitation{Invitation: invitation, InviteURL: link}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "revoke_invitation", Title: "Revoke invitation", Destructive: true,
		Description: "Cancel a pending invitation so its link no longer works. Requires the owner role.",
	}, func(_ context.Context, p core.Principal, in invitationInput) (done, error) {
		return done{OK: true}, service.organizations.RevokeInvitation(p, in.InvitationID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_invitation", Title: "Get invitation", ReadOnly: true,
		Description: "Show the organization, role, and expiry of a pending invitation from its token or link, and whether the caller can respond.",
	}, func(_ context.Context, p core.Principal, in tokenInput) (invitationPreview, error) {
		return service.Invitation(p, in.Token)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "respond_to_invitation", Title: "Respond to invitation",
		Description: "Accept or decline an invitation addressed to the caller's email. Accepting joins and selects the organization.",
	}, func(_ context.Context, p core.Principal, in respondInput) (invitationResponse, error) {
		org, err := service.RespondToInvitation(p, in.Token, in.Action)
		if err != nil {
			return invitationResponse{}, err
		}
		return invitationResponse{OrganizationID: org.ID, OrganizationName: org.Name, Action: strings.ToLower(strings.TrimSpace(in.Action))}, nil
	})
}
