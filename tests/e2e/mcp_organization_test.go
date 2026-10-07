package e2e

import (
	"slices"
	"strings"
	"testing"
)

type mcpProject struct {
	ID, Name, Region string
	Regions          []string
	IsDefault        bool
}

type mcpOrganization struct {
	ID, Name, Kind, Role, BillingStatus string
	CanManage                           bool
	Projects                            []mcpProject
}

type mcpContext struct {
	User struct {
		ID, Email string
	}
	Selection struct {
		OrganizationID, ProjectID string
	}
	Organizations []mcpOrganization
}

// TestMCPOrganizationContextProjectsAndSelection manages organizations and
// projects through MCP and checks that selection only changes on request.
func TestMCPOrganizationContextProjectsAndSelection(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("mcp-organization")
	registerAndOnboard(t, page, account)
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "organization assistant"))

	var context mcpContext
	callTool(t, session, "get_context", nil, &context)
	if context.User.Email != account.Email || context.Selection.OrganizationID == "" || context.Selection.ProjectID == "" {
		t.Fatalf("get_context = %+v, want the signed-in user and a saved selection", context)
	}
	personal := organizationByID(t, context.Organizations, context.Selection.OrganizationID)
	if personal.Kind != "personal" || personal.Role != "owner" || !personal.CanManage || len(personal.Projects) == 0 {
		t.Fatalf("personal organization = %+v, want an owned personal organization with projects", personal)
	}

	var created struct {
		ID, OrganizationID, Name string
		Regions                  []string
	}
	callTool(t, session, "create_project", map[string]any{"name": "Analytics", "region": "SFO"}, &created)
	if created.OrganizationID != personal.ID || !slices.Equal(created.Regions, []string{"SFO"}) {
		t.Fatalf("create_project = %+v, want an SFO project in %s", created, personal.ID)
	}
	callTool(t, session, "update_project", map[string]any{"projectId": created.ID, "name": "Analytics BR", "regions": []string{"GRU"}}, nil)
	callTool(t, session, "get_context", nil, &context)
	if context.Selection.ProjectID == created.ID {
		t.Fatalf("scoping a call to project %s must not change the saved selection", created.ID)
	}
	updated := projectByID(t, organizationByID(t, context.Organizations, personal.ID).Projects, created.ID)
	if updated.Name != "Analytics BR" || updated.Region != "GRU" {
		t.Errorf("updated project = %+v, want Analytics BR in GRU", updated)
	}

	var selection struct{ OrganizationID, ProjectID string }
	callTool(t, session, "select_project", map[string]any{"projectId": created.ID}, &selection)
	if selection.OrganizationID != personal.ID || selection.ProjectID != created.ID {
		t.Errorf("select_project = %+v, want %s/%s", selection, personal.ID, created.ID)
	}

	callTool(t, session, "rename_organization", map[string]any{"name": "Personal Lab"}, nil)
	var detail struct {
		Name, Kind  string
		MemberCount int
		CanLeave    bool
		Projects    []mcpProject
	}
	callTool(t, session, "get_organization", nil, &detail)
	if detail.Name != "Personal Lab" || detail.MemberCount != 1 || detail.CanLeave || len(detail.Projects) < 2 {
		t.Errorf("get_organization = %+v, want the renamed personal organization with its projects", detail)
	}

	if message := callToolError(t, session, "create_project", map[string]any{"name": "Nowhere", "region": "XXX"}); !strings.Contains(message, "unknown region") {
		t.Errorf("invalid region error = %q, want unknown region", message)
	}
	if message := callToolError(t, session, "leave_organization", nil); !strings.Contains(message, "personal") {
		t.Errorf("leave personal organization error = %q, want the personal organization rule", message)
	}
	if message := callToolError(t, session, "request_organization_deletion", map[string]any{"confirmName": "Personal Lab"}); !strings.Contains(message, "personal") {
		t.Errorf("delete personal organization error = %q, want the personal organization rule", message)
	}

	var business mcpOrganization
	callTool(t, session, "create_organization", map[string]any{"name": uniqueName("Acme")}, &business)
	if business.Kind != "business" || business.BillingStatus != "billing_required" || business.Role != "owner" {
		t.Fatalf("create_organization = %+v, want an owned business organization awaiting billing", business)
	}
	if message := callToolError(t, session, "create_project", map[string]any{"organizationId": business.ID, "name": "Launch", "region": "GRU"}); !strings.Contains(message, "billing") {
		t.Errorf("unbilled project error = %q, want the billing requirement", message)
	}
	callTool(t, session, "select_organization", map[string]any{"organizationId": personal.ID}, &selection)
	if selection.OrganizationID != personal.ID {
		t.Errorf("select_organization = %+v, want %s", selection, personal.ID)
	}
}

// TestMCPTeamInvitationsMembersAndOwnership invites a second account, then
// manages its membership and ownership through MCP from both sides.
func TestMCPTeamInvitationsMembersAndOwnership(t *testing.T) {
	t.Parallel()
	ownerPage := newPage(t)
	registerAndOnboard(t, ownerPage, newAccount("mcp-team-owner"))
	owner := connectMCP(t, createAccessToken(t, ownerPage, journeyPassword, "owner assistant"))
	var ownerContext mcpContext
	callTool(t, owner, "get_context", nil, &ownerContext)
	organizationID := ownerContext.Selection.OrganizationID

	invitee := newAccount("mcp-team-invitee")
	type invitation struct{ ID, Email, Role, Status string }
	var issued struct {
		Invitation invitation
		InviteURL  string
	}
	callTool(t, owner, "invite_member", map[string]any{"email": invitee.Email, "role": "viewer"}, &issued)
	if !strings.HasPrefix(issued.InviteURL, "/invitations/") || issued.Invitation.Status != "Pending" {
		t.Fatalf("invite_member = %+v, want a pending invitation with a link", issued)
	}
	if message := callToolError(t, owner, "invite_member", map[string]any{"email": invitee.Email}); !strings.Contains(message, "pending invitation") {
		t.Errorf("duplicate invitation error = %q, want the pending invitation conflict", message)
	}

	other := newAccount("mcp-team-other")
	var canceled struct {
		Invitation invitation
		InviteURL  string
	}
	callTool(t, owner, "invite_member", map[string]any{"email": other.Email}, &canceled)
	var resent struct{ InviteURL string }
	callTool(t, owner, "resend_invitation", map[string]any{"invitationId": canceled.Invitation.ID}, &resent)
	if resent.InviteURL == canceled.InviteURL {
		t.Errorf("resend_invitation kept the link %q, want a new one", resent.InviteURL)
	}
	callTool(t, owner, "revoke_invitation", map[string]any{"invitationId": canceled.Invitation.ID}, nil)
	var invitations struct{ Invitations []invitation }
	callTool(t, owner, "list_invitations", nil, &invitations)
	index := slices.IndexFunc(invitations.Invitations, func(i invitation) bool { return i.ID == canceled.Invitation.ID })
	if index < 0 || invitations.Invitations[index].Status != "Canceled" {
		t.Errorf("list_invitations = %+v, want %s canceled", invitations.Invitations, canceled.Invitation.ID)
	}

	inviteePage := newPage(t)
	registerAndOnboard(t, inviteePage, invitee)
	member := connectMCP(t, createAccessToken(t, inviteePage, journeyPassword, "invitee assistant"))
	var preview struct {
		OrganizationID string
		Role           string
		CanRespond     bool
	}
	callTool(t, member, "get_invitation", map[string]any{"token": baseURL + issued.InviteURL}, &preview)
	if preview.OrganizationID != organizationID || preview.Role != "viewer" || !preview.CanRespond {
		t.Fatalf("get_invitation = %+v, want a viewer invitation to %s the invitee can answer", preview, organizationID)
	}
	callTool(t, member, "respond_to_invitation", map[string]any{"token": issued.InviteURL, "action": "accept"}, nil)
	var memberContext mcpContext
	callTool(t, member, "get_context", nil, &memberContext)
	if joined := organizationByID(t, memberContext.Organizations, organizationID); joined.Role != "viewer" || joined.CanManage {
		t.Errorf("joined organization = %+v, want a viewer without management rights", joined)
	}

	type memberRow struct {
		ID, Role         string
		IsOwner, CanEdit bool
	}
	var members struct {
		Members    []memberRow
		RoleCounts struct{ Owner, Member, Viewer int }
	}
	callTool(t, owner, "list_members", map[string]any{"role": "viewer"}, &members)
	if len(members.Members) != 1 || !members.Members[0].CanEdit || members.RoleCounts.Owner != 1 || members.RoleCounts.Viewer != 1 {
		t.Fatalf("list_members(viewer) = %+v, want the editable invitee and one owner", members)
	}
	memberID := members.Members[0].ID

	scoped := map[string]any{"organizationId": organizationID, "userId": ownerContext.User.ID, "role": "viewer"}
	if message := callToolError(t, member, "update_member_role", scoped); !strings.Contains(message, "owner") {
		t.Errorf("viewer update_member_role error = %q, want the owner requirement", message)
	}
	callTool(t, owner, "update_member_role", map[string]any{"userId": memberID, "role": "member"}, nil)
	callTool(t, owner, "request_ownership_transfer", map[string]any{"userId": memberID}, nil)
	var detail struct {
		PendingOwnerUserID  string
		CanConfirmOwnership bool
		Role                string
	}
	callTool(t, member, "get_organization", map[string]any{"organizationId": organizationID}, &detail)
	if detail.PendingOwnerUserID != memberID || !detail.CanConfirmOwnership {
		t.Fatalf("get_organization for the nominee = %+v, want a transfer it can confirm", detail)
	}
	callTool(t, member, "confirm_ownership_transfer", map[string]any{"organizationId": organizationID}, nil)
	callTool(t, member, "get_organization", map[string]any{"organizationId": organizationID}, &detail)
	if detail.Role != "owner" {
		t.Errorf("role after confirming ownership = %q, want owner", detail.Role)
	}
	callTool(t, member, "remove_member", map[string]any{"organizationId": organizationID, "userId": ownerContext.User.ID}, nil)
	callTool(t, member, "list_members", map[string]any{"organizationId": organizationID}, &members)
	if len(members.Members) != 1 || members.Members[0].ID != memberID {
		t.Errorf("list_members after removal = %+v, want only the new owner", members.Members)
	}
}

func organizationByID(t *testing.T, organizations []mcpOrganization, id string) mcpOrganization {
	t.Helper()
	index := slices.IndexFunc(organizations, func(o mcpOrganization) bool { return o.ID == id })
	if index < 0 {
		t.Fatalf("organization %s is missing from %+v", id, organizations)
	}
	return organizations[index]
}

func projectByID(t *testing.T, projects []mcpProject, id string) mcpProject {
	t.Helper()
	index := slices.IndexFunc(projects, func(p mcpProject) bool { return p.ID == id })
	if index < 0 {
		t.Fatalf("project %s is missing from %+v", id, projects)
	}
	return projects[index]
}
