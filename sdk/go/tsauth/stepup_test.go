package tsauth

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestOrganizationStepUpProofs(t *testing.T) {
	t.Parallel()
	const org, user = "01ARZ3NDEKTSV4RRFFQ69G5FA3", "01ARZ3NDEKTSV4RRFFQ69G5FA7"
	var proofs []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		proofs = append(proofs, r.Header.Get(StepUpProofHeader))
		if r.Header.Get("Authorization") != "Bearer fixture-sign-in" {
			t.Errorf("authorization = %q", r.Header.Get("Authorization"))
		}
		w.Header().Set("Content-Type", "application/json")
		if r.Method == http.MethodDelete {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		_, _ = w.Write([]byte(`{"from_user_id":"` + org + `","to_user_id":"` + user +
			`","created_at":"2026-11-08T00:00:00Z","expires_at":"2026-11-09T00:00:00Z"}`))
	}))
	defer server.Close()

	client, err := New(Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	u := client.WithToken("fixture-sign-in").WithStepUpProof("fixture-proof")
	if err := u.DeleteOrganization(t.Context(), org); err != nil {
		t.Fatal(err)
	}
	transfer, err := u.StartOwnershipTransfer(t.Context(), org, user)
	if err != nil {
		t.Fatal(err)
	}
	if transfer.ToUserID != user {
		t.Fatalf("transfer = %+v", transfer)
	}
	if len(proofs) != 2 || proofs[0] != "fixture-proof" || proofs[1] != "fixture-proof" {
		t.Errorf("step-up proofs = %v", proofs)
	}
	if _, err := client.WithToken("fixture-sign-in").RequestOrganizationStepUp(t.Context(), StepUpCreateAPIKey, org); err == nil ||
		!strings.Contains(err.Error(), "organization step-up purpose") {
		t.Fatalf("wrong purpose error = %v", err)
	}
	if _, err := client.WithToken("fixture-sign-in").RequestOrganizationStepUp(t.Context(), StepUpDeleteOrganization, "not-an-ulid"); err == nil {
		t.Fatal("expected invalid organization error")
	}
}
