package v1_test

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const importFixture = `{"id":"` + parityID + `","project":"` + parityProject + `","database":"main","region":"gru","state":"ready","cancelRequested":false,"options":{"exclude":[],"collection":null},"source":{"format":"sqlite","bytes":3,"sha256":"3921e9d98be578053d67a667b4243aba8dc03e1c8fbcd8d747a8fec62cc7536a","receivedBytes":3,"staged":true,"retainedUntil":null},"preview":{"id":"` + parityID + `","tables":[{"source":"public.users","collection":"users","excluded":false,"rows":2,"primaryKey":["id"],"columns":[{"name":"id","sourceType":"integer","mapping":"integer","nullable":false,"excluded":false}],"constraints":[{"kind":"primary_key","name":null,"columns":["id"],"references":null,"enforced":false}],"issues":[]}],"skipped":[{"kind":"view","name":"summary"}],"skippedTruncated":false,"blocking":0,"warnings":1},"confirmedPreviewId":null,"progress":{"tablesDone":0,"tablesTotal":1,"rowsWritten":0,"rowsTotal":2,"currentTable":null},"validation":{"passed":true,"tables":[{"source":"public.users","collection":"users","sourceRows":2,"documents":2,"sourceChecksum":"a","documentChecksum":"a","match":true}]},"error":null,"createdAt":"2026-10-22T00:00:00Z","updatedAt":"2026-10-22T00:00:00Z","startedAt":null,"finishedAt":null}`

func checkImportFixture(t *testing.T, out *tb.ImportJob, err error) {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
	if out.ID != parityID || out.Database != "main" || out.Source.Bytes != 3 || out.Source.RetainedUntil != nil ||
		len(out.Options.Exclude) != 0 || out.Options.Collection != "" ||
		out.Preview == nil || len(out.Preview.Tables) != 1 ||
		out.Preview.Tables[0].Source != "public.users" || out.Preview.Tables[0].Collection != "users" ||
		len(out.Preview.Tables[0].PrimaryKey) != 1 || out.Preview.Tables[0].PrimaryKey[0] != "id" ||
		out.Preview.Tables[0].Constraints[0].Enforced || out.Progress.RowsWritten != 0 ||
		!out.Validation.Tables[0].Match || out.Error != nil {
		t.Fatalf("job = %+v", out)
	}
}

func TestPublicImportContracts(t *testing.T) {
	for _, endpoint := range []string{"create", "create replay", "get", "update", "start", "cancel", "resume"} {
		t.Run(endpoint, func(t *testing.T) {
			method, path, body, status := http.MethodPost, "/import", "", http.StatusAccepted
			switch endpoint {
			case "create", "create replay":
				body, status = `{"format":"sqlite","sourceBytes":3,"sourceSha256":"3921e9d98be578053d67a667b4243aba8dc03e1c8fbcd8d747a8fec62cc7536a"}`, http.StatusCreated
				if endpoint == "create replay" {
					status = http.StatusOK
				}
			case "get":
				method, status = http.MethodGet, http.StatusOK
			case "update":
				method, body = http.MethodPatch, `{"options":{"exclude":["public.audit"]}}`
			case "start":
				path, body = "/import/start", `{"previewId":"`+parityID+`"}`
			default:
				path += "/" + endpoint
			}
			d := parityClient(t, method, parityDatabase+path, "", body, importFixture, status).Database(parityProject, "main")
			var out *tb.ImportJob
			var err error
			switch endpoint {
			case "create", "create replay":
				out, err = d.CreateImport(t.Context(), tb.CreateImportRequest{Format: "sqlite", SourceBytes: 3, SourceSHA256: "3921e9d98be578053d67a667b4243aba8dc03e1c8fbcd8d747a8fec62cc7536a"})
			case "get":
				out, err = d.Import(t.Context())
			case "update":
				out, err = d.UpdateImport(t.Context(), tb.ImportOptions{Exclude: []string{"public.audit"}})
			case "start":
				out, err = d.StartImport(t.Context(), parityID)
			case "cancel":
				out, err = d.CancelImport(t.Context())
			case "resume":
				out, err = d.ResumeImport(t.Context())
			}
			checkImportFixture(t, out, err)
		})
	}
}

func TestPublicImportSourceUsesRawBoundedBytes(t *testing.T) {
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		raw, err := io.ReadAll(r.Body)
		if err != nil || string(raw) != "\x00\xffx" || r.ContentLength != 3 || r.Header.Get("Content-Type") != "application/octet-stream" || r.Header.Get("Authorization") != "Bearer header.project.signature" || r.Method != http.MethodPut || r.URL.Path != parityDatabase+"/import/source" || r.URL.RawQuery != "offset=0" {
			t.Error("incorrect binary upload request")
		}
		io.WriteString(w, importFixture)
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	d := c.Database(parityProject, "main")
	out, err := d.UploadImportSource(t.Context(), 0, []byte{0, 255, 'x'})
	checkImportFixture(t, out, err)
	for _, part := range [][]byte{nil, make([]byte, (4<<20)+1)} {
		if _, err := d.UploadImportSource(t.Context(), 0, part); err == nil {
			t.Error("invalid part accepted")
		}
	}
	if _, err := d.UploadImportSource(t.Context(), -1, []byte{1}); err == nil {
		t.Error("negative offset accepted")
	}
	if calls != 1 {
		t.Fatalf("requests = %d", calls)
	}
}

func TestPublicImportConfirmationPropagatesPreviewChanged(t *testing.T) {
	c := parityClient(t, http.MethodPost, parityDatabase+"/import/start", "", `{"previewId":"`+parityID+`"}`, `{"error":{"code":"preview_changed","message":"confirm the current preview"}}`, http.StatusConflict)
	_, err := c.Database(parityProject, "main").StartImport(t.Context(), parityID)
	if !errors.Is(err, tb.ErrorCode("preview_changed")) {
		t.Fatalf("error = %v", err)
	}
}

// The immutable producer DTO pin for these fixtures is in contracts/import-dtos.md.
func TestPublicImportOptionsContract(t *testing.T) {
	for _, tc := range []struct {
		name    string
		options tb.ImportOptions
		wire    string
	}{
		{"defaults", tb.ImportOptions{}, `{}`},
		{"exclude", tb.ImportOptions{Exclude: []string{"public.audit"}}, `{"exclude":["public.audit"]}`},
		{"collection", tb.ImportOptions{Collection: "imported_docs"}, `{"collection":"imported_docs"}`},
		{"both", tb.ImportOptions{Exclude: []string{"public.audit"}, Collection: "imported_docs"}, `{"exclude":["public.audit"],"collection":"imported_docs"}`},
	} {
		for _, method := range []string{http.MethodPost, http.MethodPatch} {
			t.Run(tc.name+"/"+method, func(t *testing.T) {
				body, status := `{"options":`+tc.wire+`}`, http.StatusAccepted
				const hash = "3921e9d98be578053d67a667b4243aba8dc03e1c8fbcd8d747a8fec62cc7536a"
				if method == http.MethodPost {
					body = `{"format":"ndjson","sourceBytes":3,"sourceSha256":"` + hash + `","options":` + tc.wire + `}`
					status = http.StatusCreated
				}
				d := parityClient(t, method, parityDatabase+"/import", "", body, importFixture, status).Database(parityProject, "main")
				var out *tb.ImportJob
				var err error
				if method == http.MethodPost {
					out, err = d.CreateImport(t.Context(), tb.CreateImportRequest{Format: "ndjson", SourceBytes: 3, SourceSHA256: hash, Options: &tc.options})
				} else {
					out, err = d.UpdateImport(t.Context(), tc.options)
				}
				checkImportFixture(t, out, err)
			})
		}
	}
}

func TestPublicImportTableProducerRoundTrip(t *testing.T) {
	var fixture struct {
		Preview struct {
			Tables []map[string]any `json:"tables"`
		} `json:"preview"`
	}
	if err := json.Unmarshal([]byte(importFixture), &fixture); err != nil {
		t.Fatal(err)
	}
	d := parityClient(t, http.MethodGet, parityDatabase+"/import", "", "", importFixture, http.StatusOK).Database(parityProject, "main")
	out, err := d.Import(t.Context())
	checkImportFixture(t, out, err)
	raw, err := json.Marshal(out.Preview.Tables[0])
	if err != nil {
		t.Fatal(err)
	}
	var table map[string]any
	if err := json.Unmarshal(raw, &table); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(table, fixture.Preview.Tables[0]) {
		t.Fatalf("table changed producer fields: got %s; want %v", raw, fixture.Preview.Tables[0])
	}
}
