package v1_test

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

// HTTP fixtures verify the SDK contract, not the producer's rename invariants.
func TestRenameDatabaseContract(t *testing.T) {
	t.Parallel()
	const databaseID = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		body, _ := io.ReadAll(r.Body)
		path := "/v1/projects/" + testProject + "/databases/" + databaseID
		if calls == 3 {
			path += "/collections/orders/documents/by-key"
		}
		method := http.MethodGet
		if calls == 1 {
			method = http.MethodPatch
		}
		if r.Method != method || r.URL.Path != path || (calls == 1 && string(body) != `{"name":"orders_new"}`) {
			t.Errorf("request: %s %s %s", r.Method, r.URL, body)
		}
		if r.Header.Get("Authorization") != "Bearer header.project.signature" || (calls == 1 && r.Header.Get("Content-Type") != "application/json") || r.Header.Get("Idempotency-Key") != "" {
			t.Errorf("unexpected request headers")
		}
		w.Header().Set("Content-Type", "application/json")
		if calls == 3 {
			if r.URL.Query().Get("id") != databaseID {
				t.Error("document key changed")
			}
			io.WriteString(w, `{"id":"`+databaseID+`","version":"tbv1-00000000000000000000000000000000","state":"value","document":{"kept":true}}`)
			return
		}
		io.WriteString(w, `{"id":"`+databaseID+`","projectId":"`+testProject+`","name":"orders_new","homeRegion":"gru","replicaRegions":["fra"],"allRegions":false}`)
	}))
	defer server.Close()
	client, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	out, err := client.RenameDatabase(t.Context(), testProject, databaseID, "orders_new")
	if err != nil {
		t.Fatal(err)
	}
	if out.ID != databaseID || out.ProjectID != testProject || out.Name != "orders_new" || (out.HomeRegion == nil || *out.HomeRegion != "gru") || len(out.ReplicaRegions) != 1 || out.ReplicaRegions[0] != "fra" || out.AllRegions || calls != 1 {
		t.Fatalf("rename response: %+v calls=%d", out, calls)
	}
	info, err := client.Database(testProject, databaseID).Info(t.Context())
	if err != nil || info.Name != "orders_new" {
		t.Fatalf("reconcile: %+v %v", info, err)
	}
	doc, err := client.Database(testProject, databaseID).Collection("orders").Get(t.Context(), databaseID)
	if err != nil || string(doc.Document) != `{"kept":true}` || calls != 3 {
		t.Fatalf("read by ID: %+v %v calls=%d", doc, err, calls)
	}
}

func TestRenameDatabaseErrorsAreNotRetried(t *testing.T) {
	t.Parallel()
	for _, status := range []int{400, 401, 403, 404, 409, 421, 429, 503} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			t.Parallel()
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				w.WriteHeader(status)
				io.WriteString(w, `{"error":{"code":"storage_unavailable","message":"fixture refusal"}}`)
			}))
			defer server.Close()
			client, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
			if err != nil {
				t.Fatal(err)
			}
			_, err = client.RenameDatabase(t.Context(), testProject, "01ARZ3NDEKTSV4RRFFQ69G5FAW", "orders_new")
			var api *tb.APIError
			if !errors.As(err, &api) || api.Status != status || calls != 1 {
				t.Fatalf("error: %v calls=%d", err, calls)
			}
		})
	}
}

func TestRenameDatabaseRejectsInvalidInputsBeforeHTTP(t *testing.T) {
	t.Parallel()
	client, err := tb.New(tb.Config{BaseURL: "https://tb.example", Token: "header.project.signature", HTTPClient: &http.Client{Transport: failTransport{t}}})
	if err != nil {
		t.Fatal(err)
	}
	for _, input := range [][3]string{
		{"project-name", testProject, "orders_new"},
		{testProject, "orders", "orders_new"},
		{testProject, testProject, "Bad Name"},
		{testProject, testProject, ""},
	} {
		if _, err := client.RenameDatabase(t.Context(), input[0], input[1], input[2]); err == nil {
			t.Errorf("accepted invalid input: %q", input)
		}
	}
	for _, selector := range []string{"", "../escape", "Bad Name", "81ARZ3NDEKTSV4RRFFQ69G5FAW", "01arz3ndektsv4rrffq69g5faw"} {
		if _, err := client.Database(testProject, selector).Info(t.Context()); err == nil {
			t.Errorf("accepted malformed selector %q", selector)
		}
		if _, err := client.Database(testProject, selector).Collection("orders").Get(t.Context(), testProject); err == nil {
			t.Errorf("accepted malformed data selector %q", selector)
		}
	}
	if _, err := client.Database(testProject, testProject).Create(t.Context()); err == nil {
		t.Error("accepted a ULID as a new database name")
	}
	if _, err := client.Database(testProject, testProject).CreateIn(t.Context(), tb.Placement{}); err == nil {
		t.Error("accepted a ULID as a placed database name")
	}
}

func TestDatabaseConflictMetadataAndRenameClearing(t *testing.T) {
	const id, winner = "01ARZ3NDEKTSV4RRFFQ69G5FAW", "01ARZ3NDEKTSV4RRFFQ69G5FAV"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet {
			io.WriteString(w, `{"items":[{"id":"`+id+`","projectId":"`+testProject+`","name":"orders-conflict-tail","previousName":"orders","conflictOf":"`+winner+`"}]}`)
		} else {
			io.WriteString(w, `{"id":"`+id+`","projectId":"`+testProject+`","name":"orders_new","previousName":null,"conflictOf":null}`)
		}
	}))
	defer server.Close()
	client, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature"})
	if err != nil {
		t.Fatal(err)
	}
	page, err := client.Databases(t.Context(), testProject, tb.PageOptions{})
	if err != nil || len(page.Items) != 1 || page.Items[0].PreviousName == nil || *page.Items[0].PreviousName != "orders" || page.Items[0].ConflictOf == nil || *page.Items[0].ConflictOf != winner {
		t.Fatalf("conflict metadata: %+v, %v", page, err)
	}
	renamed, err := client.RenameDatabase(t.Context(), testProject, id, "orders_new")
	if err != nil || renamed.PreviousName != nil || renamed.ConflictOf != nil {
		t.Fatalf("cleared metadata: %+v, %v", renamed, err)
	}
}
