package v1_test

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestSetPatchOperationsAndUnknownOutcome(t *testing.T) {
	for _, unknown := range []bool{false, true} {
		t.Run(map[bool]string{false: "committed", true: "unknown"}[unknown], func(t *testing.T) {
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				raw, err := io.ReadAll(r.Body)
				if err != nil || string(raw) != `{"ops":[{"op":"add","path":"/tags","value":"new"},{"op":"remove","path":"/tags","value":"old"}]}` || r.Method != http.MethodPatch || r.URL.Path != parityDatabase+"/collections/users/documents/by-key" || r.URL.Query().Get("id") != "01ARZ3NDEKTSV4RRFFQ69G5FD5" || r.Header.Get("Content-Type") != "application/vnd.terrabase.ops+json" {
					t.Error("incorrect set patch")
				}
				if unknown {
					w.WriteHeader(http.StatusServiceUnavailable)
					io.WriteString(w, `{"error":{"code":"write_outcome_unknown","message":"confirmation unknown","region":"gru","machine":"machine-1"}}`)
				} else {
					io.WriteString(w, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD5","version":"opaque","offset":"1","durability":"remote_durable","document":{"tags":["new"]}}`)
				}
			}))
			t.Cleanup(server.Close)
			c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client(), MaxRetries: 3})
			if err != nil {
				t.Fatal(err)
			}
			out, err := c.Database(parityProject, "main").Collection("users").Patch(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD5", tb.Ops(tb.Add("/tags", "new"), tb.Remove("/tags", "old")), tb.IfMatch("opaque"))
			if unknown {
				var api *tb.APIError
				if !errors.As(err, &api) || !errors.Is(err, tb.ErrWriteOutcomeUnknown) || api.Region != "gru" || api.Machine != "machine-1" {
					t.Fatalf("unknown = %v", err)
				}
			} else if err != nil || out.Durability != tb.RemoteDurable || string(out.Document) != `{"tags":["new"]}` {
				t.Fatalf("patch = %+v, %v", out, err)
			}
			if calls != 1 {
				t.Fatalf("requests = %d; uncertain mutations must not be replayed", calls)
			}
		})
	}
}

func TestPublicUsagePreservesProjectAndCollectionCounts(t *testing.T) {
	for _, database := range []bool{false, true} {
		path := "/v1/projects/" + parityProject
		body := `{"projectId":"` + parityProject + `","databases":2,"storageBytes":8,"logicalBytes":4,"documents":3,"reads":1,"writes":2,"period":null,"series":[],"items":[{"databaseId":"` + parityID + `","collections":7,"storageBytes":8,"logicalBytes":4,"documents":3,"reads":1,"writes":2}]}`
		if database {
			path, body = parityDatabase, `{"databaseId":"`+parityID+`","collections":7,"storageBytes":8,"logicalBytes":4,"documents":3,"reads":1,"writes":2}`
		}
		c := parityClient(t, http.MethodGet, path+"/usage", "", "", body, http.StatusOK)
		var out *tb.Usage
		var err error
		if database {
			out, err = c.Database(parityProject, "main").Usage(t.Context(), tb.UsageOptions{})
		} else {
			out, err = c.ProjectUsage(t.Context(), parityProject, tb.UsageOptions{})
		}
		if err != nil {
			t.Fatal(err)
		}
		if database {
			if out.Collections != 7 {
				t.Fatal("collection count lost")
			}
		} else if out.Databases != 2 || out.Items[0].Collections != 7 {
			t.Fatal("project or nested collection count lost")
		}
	}
}

func TestPublicRecommendationEscapesProducerIdentifier(t *testing.T) {
	const id = "users:email"
	path := parityDatabase + "/index-recommendations/" + url.PathEscape(id) + "/apply"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.EscapedPath() != path || r.RequestURI != path || r.URL.RawQuery != "" || r.Method != http.MethodPost {
			t.Errorf("recommendation request = %s %s", r.Method, r.RequestURI)
		}
		io.WriteString(w, `{"status":"applied","index":{"name":"users_email","collection":"users","fields":["email"],"state":"active"}}`)
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	out, err := c.Database(parityProject, "main").ApplyIndexRecommendation(t.Context(), id)
	if err != nil || out.Index.Name != "users_email" {
		t.Fatalf("apply = %+v, %v", out, err)
	}
}

func TestSchemaValidationErrorPreservesBoundedDetails(t *testing.T) {
	c := parityClient(t, http.MethodPut, parityDatabase+"/collections/users/documents/by-key", "id=01ARZ3NDEKTSV4RRFFQ69G5FD5", `{"name":1}`, `{"error":{"code":"schema_validation_failed","message":"document does not match schema","details":[{"path":"/name","keyword":"type"}]}}`, http.StatusBadRequest)
	_, err := c.Database(parityProject, "main").Collection("users").Put(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD5", map[string]int{"name": 1})
	var api *tb.APIError
	if !errors.As(err, &api) || api.Code != "schema_validation_failed" || len(api.Details) != 1 || api.Details[0].Path != "/name" || api.Details[0].Keyword != "type" {
		t.Fatalf("validation = %v", err)
	}
}
