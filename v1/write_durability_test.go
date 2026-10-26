package v1_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

func TestWriteDurabilityGuaranteesPreservePreconditions(t *testing.T) {
	for _, guarantee := range []tb.Durability{"", tb.LocalDurable, tb.RemoteDurable} {
		for _, operation := range []string{"create", "put", "patch", "delete", "tx"} {
			t.Run(string(guarantee)+"/"+operation, func(t *testing.T) {
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					if r.Header.Get("TB-Durability") != string(guarantee) {
						t.Error("missing request guarantee")
					}
					if operation == "put" && r.Header.Get("If-Match") != `"opaque"` {
						t.Error("precondition lost")
					}
					w.Header().Set("Content-Type", "application/json")
					if operation == "tx" {
						var body struct{ Ops []map[string]any }
						if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Ops[0]["if_match"] != `"opaque"` {
							t.Error("transaction precondition lost")
						}
						fmt.Fprintf(w, `{"results":[{"id":%q,"version":"opaque"}],"offset":"1","durability":"local_durable"}`, testProject)
					} else {
						fmt.Fprintf(w, `{"id":%q,"version":"opaque","document":{},"offset":"1","durability":"local_durable"}`, testProject)
					}
				}))
				defer server.Close()
				c, err := tb.New(tb.Config{BaseURL: server.URL, Token: testToken})
				if err != nil {
					t.Fatal(err)
				}
				db := c.Database(testProject, "main")
				col := db.Collection("t")
				switch operation {
				case "create":
					_, err = col.Create(t.Context(), map[string]any{}, guarantee)
				case "put":
					_, err = col.Put(t.Context(), testProject, map[string]any{}, tb.IfMatch("opaque"), guarantee)
				case "patch":
					_, err = col.Patch(t.Context(), testProject, tb.MergePatch(map[string]any{}), guarantee)
				case "delete":
					_, err = col.Delete(t.Context(), testProject, guarantee)
				case "tx":
					_, err = db.Tx().Put("t", testProject, map[string]any{}, tb.IfMatch("opaque")).WithDurability(guarantee).Commit(t.Context())
				}
				if err != nil {
					t.Fatal(err)
				}
			})
		}
	}
}

func TestDurabilityUnknownOutcomeIsNotReplayed(t *testing.T) {
	users, _, transport := scriptedUsers(t, scripted{status: 503, body: `{"error":{"code":"write_outcome_unknown","region":"gru","machine":"fixture"}}`})
	_, err := users.Put(t.Context(), testProject, map[string]any{}, tb.IfMatch("opaque"), tb.RemoteDurable)
	var api *tb.APIError
	if !errors.Is(err, tb.ErrCommitUncertain) || !errors.As(err, &api) || api.Region != "gru" || len(transport.requests) != 1 {
		t.Fatal(err)
	}
	for _, d := range []tb.Durability{tb.ObjectDurable, tb.Unchanged, "invalid"} {
		_, err := users.Put(t.Context(), testProject, map[string]any{}, d)
		if err == nil || len(transport.requests) != 1 {
			t.Fatal("invalid guarantee reached transport")
		}
	}
}

func TestDurabilityPreExecutionRefusalAndCancellation(t *testing.T) {
	users, db, transport := scriptedUsers(t, scripted{status: 503, body: `{"error":{"code":"remote_region_unavailable"}}`, header: http.Header{"Retry-After": {"120"}}})
	_, err := users.Put(t.Context(), testProject, map[string]any{}, tb.RemoteDurable)
	var api *tb.APIError
	if !errors.As(err, &api) || api.Code != tb.ErrRemoteRegionUnavailable || api.RetryAfter != 120*time.Second || errors.Is(err, tb.ErrCommitUncertain) {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := users.Create(ctx, map[string]any{}, tb.RemoteDurable); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if _, err := db.Tx().Put("t", testProject, map[string]any{}).WithDurability(tb.RemoteDurable).Commit(ctx); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if len(transport.requests) != 1 {
		t.Fatal("cancellation dispatched a mutation")
	}
}
