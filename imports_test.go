package sdk

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestImportRoutesReachPublishedContract(t *testing.T) {
	job := `{"operation_id":"import-1","tenant_id":"tenant-1","project_id":"project-1","database_id":"demo","format":"jsonl","durability":"regional","input_committed":false,"cursor":0,"committed_records":0,"state":"uploading"}`
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.Method == http.MethodPost && r.URL.Path == "/api/v1/databases/demo/imports":
			var request struct {
				Format         string `json:"format"`
				IdempotencyKey string `json:"idempotency_key"`
			}
			if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
				t.Errorf("create body: %v", err)
			}
			if request.Format != "jsonl" || request.IdempotencyKey != "import-1" || r.Header.Get("Idempotency-Key") != "import-1" {
				t.Errorf("create request = %+v, idempotency=%q", request, r.Header.Get("Idempotency-Key"))
			}
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(job))
		case r.Method == http.MethodGet && r.URL.Path == "/api/v1/databases/demo/imports":
			_, _ = w.Write([]byte("[" + job + "]"))
		case r.Method == http.MethodGet && r.URL.Path == "/api/v1/databases/demo/imports/import-1":
			_, _ = w.Write([]byte(job))
		case r.Method == http.MethodPut && r.URL.Path == "/api/v1/databases/demo/imports/import-1/input":
			body, err := io.ReadAll(r.Body)
			if err != nil {
				t.Errorf("upload body: %v", err)
			}
			if r.Header.Get("Content-Type") != "application/octet-stream" || !bytes.Equal(body, []byte("one\n")) {
				t.Errorf("upload request content-type=%q body=%q", r.Header.Get("Content-Type"), body)
			}
			_, _ = w.Write([]byte(job))
		case r.Method == http.MethodPost && r.URL.Path == "/api/v1/databases/demo/imports/import-1/resume":
			_, _ = w.Write([]byte(job))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	ctx := t.Context()
	created, err := client.Databases.CreateImport(ctx, "demo", TransferFormatJSONL, "import-1")
	if err != nil || created.OperationID != "import-1" {
		t.Fatalf("CreateImport = %+v, %v", created, err)
	}
	listed, err := client.Databases.ListImports(ctx, "demo")
	if err != nil || len(listed) != 1 || listed[0].OperationID != "import-1" {
		t.Fatalf("ListImports = %+v, %v", listed, err)
	}
	fetched, err := client.Databases.GetImport(ctx, "demo", "import-1")
	if err != nil || fetched.State != "uploading" {
		t.Fatalf("GetImport = %+v, %v", fetched, err)
	}
	if _, err := client.Databases.UploadImport(ctx, "demo", "import-1", []byte("one\n")); err != nil {
		t.Fatalf("UploadImport: %v", err)
	}
	if _, err := client.Databases.ResumeImport(ctx, "demo", "import-1"); err != nil {
		t.Fatalf("ResumeImport: %v", err)
	}
}

func TestCreateImportValidatesFormatAndIdentityBeforeHTTP(t *testing.T) {
	client, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:1"})
	if err != nil {
		t.Fatal(err)
	}
	ctx := t.Context()
	if _, err := client.Databases.CreateImport(ctx, "demo", TransferFormatNative, "import-1"); err == nil {
		t.Fatal("native import was accepted")
	}
	if _, err := client.Databases.CreateImport(ctx, "demo", TransferFormatJSONL, ""); err == nil {
		t.Fatal("blank import identity was accepted")
	}
}
