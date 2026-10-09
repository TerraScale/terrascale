package access

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

var testNow = time.Date(2026, time.September, 26, 12, 0, 0, 0, time.UTC)

func TestLegacyAccessRedirect(t *testing.T) {
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Tools: core.NewTools(nil)}, nil)
	for _, tc := range []struct {
		query string
		tab   string
	}{
		{"", "members"},
		{"tab=members&role=admin&q=Alice&page=2", "members"},
		{"tab=invitations&create=1", "invitations"},
		{"tab=invitations&page=2", "invitations"},
		{"create=1", "invitations"},
		{"tab=unknown", "members"},
		{"tab=unknown&create=1", "invitations"},
		{"tab=members&create=1", "members"},
	} {
		t.Run(tc.query, func(t *testing.T) {
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/access?"+tc.query, nil))
			location, err := url.Parse(response.Header().Get("Location"))
			if err != nil || response.Code != http.StatusSeeOther || location.Path != "/organization" {
				t.Fatalf("redirect = %d %q, error %v", response.Code, response.Header().Get("Location"), err)
			}
			want, _ := url.ParseQuery(tc.query)
			want.Set("tab", tc.tab)
			if got := location.Query().Encode(); got != want.Encode() {
				t.Errorf("redirect query = %q, want %q", got, want.Encode())
			}
		})
	}
}

func teamOrganization(invitations ...core.Invitation) core.Organization {
	org := core.Organization{
		ID: "1MZ7GT1142NZMAQXJE73TYTFZW", Name: "Acme", OwnerUserID: "36W5Q3XPHDVYZ1GYK1WJ2S4RKK",
		Members: map[string]core.Member{
			"36W5Q3XPHDVYZ1GYK1WJ2S4RKK": {UserID: "36W5Q3XPHDVYZ1GYK1WJ2S4RKK", Role: "owner", Status: "active"},
			"21YDES3QSMXC1HQKX9PXCZ2PY0": {UserID: "21YDES3QSMXC1HQKX9PXCZ2PY0", Role: "admin", Status: "active"},
			"3815H39Y2X0TANA4N0YVSSWWH3": {UserID: "3815H39Y2X0TANA4N0YVSSWWH3", Role: "member", Status: "active"},
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
	p := core.Principal{UserID: "36W5Q3XPHDVYZ1GYK1WJ2S4RKK", OrganizationID: org.ID}
	return buildPageData(core.Account{}, org, p, values, "", "", testNow)
}

func TestInvitationsPastExpiryAreExpired(t *testing.T) {
	org := teamOrganization(
		core.Invitation{ID: "6EP4659GS4RHPHP5SK6D0FM91A", Email: "open@example.test", Role: "member", Status: "pending", ExpiresAt: testNow.Add(time.Hour)},
		core.Invitation{ID: "7GBHH6VYX41N72BV3RHEQ2YB5P", Email: "lapsed@example.test", Role: "viewer", Status: "pending", ExpiresAt: testNow.Add(-time.Hour)},
		core.Invitation{ID: "7T0XH2NMJKAD2M2TNCBR531NJD", Email: "forever@example.test", Role: "member", Status: "pending"},
		core.Invitation{ID: "5SBRH9FRXEX17CDYQD2ET8ZJ5D", Email: "revoked@example.test", Role: "member", Status: "revoked", ExpiresAt: testNow.Add(time.Hour)},
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
	if data.PreviousURL != "/organization?page=1&tab=invitations" || data.NextURL != "" {
		t.Fatalf("pager links = %q, %q", data.PreviousURL, data.NextURL)
	}
}

func TestMembersPageKeepsBothListsAndTheirPagination(t *testing.T) {
	org := teamOrganization()
	org.Members = make(map[string]core.Member)
	for i := range 12 {
		id := fmt.Sprintf("usr_%02d", i)
		email := fmt.Sprintf("user%02d@example.test", i)
		org.Members[id] = core.Member{UserID: id, Email: email, Name: id, Role: "member", Status: "active"}
		org.Invitations[id] = core.Invitation{ID: id, Email: email, Role: "viewer", Status: "pending"}
	}
	p := core.Principal{UserID: "36W5Q3XPHDVYZ1GYK1WJ2S4RKK", OrganizationID: org.ID}
	for _, tc := range []struct {
		tab                  string
		members, invitations int
	}{
		{"members", 2, 10},
		{"invitations", 10, 2},
	} {
		t.Run(tc.tab, func(t *testing.T) {
			query := url.Values{"tab": {tc.tab}, "page": {"2"}}
			members, invitations := buildMembersPageData(core.Account{}, org, p, query, "", "", testNow)
			if len(members.Members) != tc.members || len(invitations.Invitations) != tc.invitations {
				t.Fatalf("page 2 of %s has %d members and %d invitations, want %d and %d", tc.tab, len(members.Members), len(invitations.Invitations), tc.members, tc.invitations)
			}
			if members.PageCount != 2 || invitations.PageCount != 2 {
				t.Fatalf("both lists must retain their own pager: members=%+v invitations=%+v", members, invitations)
			}
		})
	}
	query := url.Values{"tab": {"members"}, "q": {"user11"}, "role": {"all", "member"}}
	members, invitations := buildMembersPageData(core.Account{}, org, p, query, "", "", testNow)
	if len(members.Members) != 1 || members.Members[0].Email != "user11@example.test" || len(invitations.Invitations) != 10 {
		t.Fatalf("member filters must leave invitations visible: members=%+v invitations=%+v", members.Members, invitations.Invitations)
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
	if members := teamPage(teamOrganization(), "tab=members&role=admin").Members; len(members) != 1 || members[0].ID != "21YDES3QSMXC1HQKX9PXCZ2PY0" {
		t.Errorf("admin filter = %+v, want only 21YDES3QSMXC1HQKX9PXCZ2PY0", members)
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

func TestRedirectAccessErrorNamesAStoreOutage(t *testing.T) {
	response := httptest.NewRecorder()
	redirectAccessError(response, httptest.NewRequest(http.MethodPost, "/access?tab=members", nil), fmt.Errorf("revoke: %w", core.ErrUnavailable))
	location, err := url.Parse(response.Header().Get("Location"))
	if err != nil || response.Code != http.StatusSeeOther {
		t.Fatalf("redirect = %d %q", response.Code, response.Header().Get("Location"))
	}
	if got := location.Query().Get("error"); got != "The team service is unavailable right now. Try again in a moment." {
		t.Errorf("outage message = %q", got)
	}
}
