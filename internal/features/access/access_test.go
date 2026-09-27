package access

import (
	"fmt"
	"net/http"
	"net/url"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

var testNow = time.Date(2026, time.September, 26, 12, 0, 0, 0, time.UTC)

func teamOrganization(invitations ...core.Invitation) core.Organization {
	org := core.Organization{
		ID: "org_1", Name: "Acme", OwnerUserID: "usr_owner",
		Members: map[string]core.Member{
			"usr_owner":  {UserID: "usr_owner", Role: "owner", Status: "active"},
			"usr_admin":  {UserID: "usr_admin", Role: "admin", Status: "active"},
			"usr_member": {UserID: "usr_member", Role: "member", Status: "active"},
		},
		Invitations: make(map[string]core.Invitation),
	}
	for _, invitation := range invitations {
		org.Invitations[invitation.ID] = invitation
	}
	return org
}

func teamPage(org core.Organization, query string) pageData {
	values, _ := url.ParseQuery(query)
	p := core.Principal{UserID: "usr_owner", OrganizationID: org.ID}
	return buildPageData(core.Account{}, org, p, values, "", "", testNow)
}

func TestInvitationsPastExpiryAreExpired(t *testing.T) {
	org := teamOrganization(
		core.Invitation{ID: "inv_open", Email: "open@example.test", Role: "member", Status: "pending", ExpiresAt: testNow.Add(time.Hour)},
		core.Invitation{ID: "inv_lapsed", Email: "lapsed@example.test", Role: "viewer", Status: "pending", ExpiresAt: testNow.Add(-time.Hour)},
		core.Invitation{ID: "inv_forever", Email: "forever@example.test", Role: "member", Status: "pending"},
		core.Invitation{ID: "inv_revoked", Email: "revoked@example.test", Role: "member", Status: "revoked", ExpiresAt: testNow.Add(time.Hour)},
	)
	data := teamPage(org, "tab=invitations")
	if data.PendingInviteCount != 2 {
		t.Errorf("PendingInviteCount = %d, want 2: a lapsed link is not pending", data.PendingInviteCount)
	}
	want := map[string]invitationView{
		"open@example.test":    {Role: "member", Status: "Pending", ExpiresAt: "2026-09-26T13:00:00Z", ExpiresOn: "Sep 26, 2026", Open: true},
		"lapsed@example.test":  {Role: "viewer", Status: "Expired", ExpiresAt: "2026-09-26T11:00:00Z", ExpiresOn: "Sep 26, 2026", Open: true},
		"forever@example.test": {Role: "member", Status: "Pending", ExpiresOn: "No expiry", Open: true},
		"revoked@example.test": {Role: "member", Status: "Canceled", ExpiresAt: "2026-09-26T13:00:00Z", ExpiresOn: "Sep 26, 2026"},
	}
	for _, got := range data.Invitations {
		expected := want[got.Email]
		if got.Role != expected.Role || got.Status != expected.Status || got.ExpiresAt != expected.ExpiresAt || got.ExpiresOn != expected.ExpiresOn || got.Open != expected.Open {
			t.Errorf("%s = %+v, want %+v", got.Email, got, expected)
		}
	}
}

func TestInvitationsTabIsPaged(t *testing.T) {
	invitations := make([]core.Invitation, 12)
	for i := range invitations {
		invitations[i] = core.Invitation{ID: fmt.Sprintf("inv_%02d", i), Email: fmt.Sprintf("user%02d@example.test", i), Role: "member", Status: "pending"}
	}
	data := teamPage(teamOrganization(invitations...), "tab=invitations&page=2")
	if len(data.Invitations) != 2 || data.First != 11 || data.Last != 12 || data.Total != 12 {
		t.Fatalf("page 2 = %d invitations, %d–%d of %d; want 2, 11–12 of 12", len(data.Invitations), data.First, data.Last, data.Total)
	}
	if data.PreviousURL != "/access?page=1&tab=invitations" || data.NextURL != "" {
		t.Fatalf("pager links = %q, %q", data.PreviousURL, data.NextURL)
	}
}

func TestRoleSharesCountAdmins(t *testing.T) {
	data := teamPage(teamOrganization(), "")
	total := 0
	for _, share := range data.RoleShares {
		total += share.Count
		if share.Label == "Admin" && share.Count != 1 {
			t.Errorf("Admin share = %d, want 1", share.Count)
		}
	}
	if total != 3 {
		t.Errorf("role shares cover %d members, want all 3", total)
	}
	if members := teamPage(teamOrganization(), "tab=members&role=admin").Members; len(members) != 1 || members[0].ID != "usr_admin" {
		t.Errorf("admin filter = %+v, want only usr_admin", members)
	}
}

func TestInvitationNotice(t *testing.T) {
	if got := teamPage(teamOrganization(), "invitation=accepted").Notice; got.Tone != "success" || got.Message != "You are now a member of Acme." {
		t.Errorf("accepted notice = %+v", got)
	}
	if got := teamPage(teamOrganization(), "invitation=declined").Notice; got.Tone != "info" {
		t.Errorf("declined notice = %+v", got)
	}
	if got := teamPage(teamOrganization(), "invitation=forged").Notice; got != (notice{}) {
		t.Errorf("unknown outcome notice = %+v, want none", got)
	}
}

func TestRespondError(t *testing.T) {
	for _, tc := range []struct {
		err     error
		status  int
		message bool
	}{
		{err: core.ErrForbidden, status: http.StatusForbidden},
		{err: core.ErrConflict, status: http.StatusConflict, message: true},
		{err: ErrInvalidInvitationAction, status: http.StatusBadRequest, message: true},
		{err: core.ErrInvalid, status: http.StatusInternalServerError, message: true},
	} {
		message, status := respondError(tc.err, "Acme")
		if status != tc.status || (message != "") != tc.message {
			t.Errorf("respondError(%v) = %q, %d; want status %d, message %t", tc.err, message, status, tc.status, tc.message)
		}
	}
}
