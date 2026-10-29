package v1_test

import (
	"net/http"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

func TestSelectiveRecoveryCustomerPreviewContract(t *testing.T) {
	t.Parallel()
	const at = "2026-10-25T10:00:00Z"
	response := `{"approval":"fake-approval","expiresAt":"2026-10-25T10:10:00Z","records":[{"collection":"orders","id":"` + parityID + `","historical":{"n":9007199254740993},"current":null,"currentVersion":"tombstone-etag"}]}`
	db := parityClient(t, http.MethodPost, parityDatabase+"/point-in-time/recovery-preview", "",
		`{"pointInTime":"`+at+`","destination":"customer","selections":[{"collection":"orders"}]}`, response, http.StatusOK).Database(parityProject, "main")
	instant, err := time.Parse(time.RFC3339, at)
	if err != nil {
		t.Fatal(err)
	}
	preview, err := db.PreviewSelectiveRecovery(t.Context(), tb.RecoveryPreviewRequest{
		PointInTime: instant, Destination: "customer", Selections: []tb.RecoverySelection{{Collection: "orders"}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if preview.Approval != "fake-approval" || len(preview.Records) != 1 || preview.ExpiresAt.IsZero() {
		t.Fatal("invalid preview envelope")
	}
	r := preview.Records[0]
	if string(r.Historical) != `{"n":9007199254740993}` || string(r.Current) != "null" || r.CurrentVersion == nil || *r.CurrentVersion != "tombstone-etag" {
		t.Fatal("preview lost historical precision or a tombstone validator")
	}
}
