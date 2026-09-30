package sdk

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"slices"
	"strings"
	"testing"
)

func assertProducerJSON(t *testing.T, actual, expected []byte) {
	t.Helper()
	var a, b any
	decode := func(raw []byte, value *any) {
		decoder := json.NewDecoder(bytes.NewReader(raw))
		decoder.UseNumber()
		if err := decoder.Decode(value); err != nil {
			t.Fatal(err)
		}
	}
	decode(actual, &a)
	decode(expected, &b)
	if !reflect.DeepEqual(a, b) {
		t.Fatalf("JSON differs from producer: got %s, want %s", actual, expected)
	}
}

func pinnedNativeContractRoot(t testing.TB) string {
	t.Helper()
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime.Caller failed")
	}
	return filepath.Join(filepath.Dir(file), "contracts", "native")
}

func readPinnedNativeJSON(t testing.TB, relative string, dst any) []byte {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(pinnedNativeContractRoot(t), relative))
	if err != nil {
		t.Fatalf("read native producer artifact %s: %v", relative, err)
	}
	if err := json.Unmarshal(raw, dst); err != nil {
		t.Fatalf("parse native producer artifact %s: %v", relative, err)
	}
	return raw
}

func TestNativeProducerArtifactProvenance(t *testing.T) {
	var manifest struct {
		SchemaVersion   int    `json:"schema_version"`
		ContractVersion string `json:"contract_version"`
		Producer        string `json:"producer"`
		Qualification   string `json:"qualification"`
		Artifacts       []struct {
			Path         string `json:"path"`
			ProducerPath string `json:"producer_path"`
			SHA256       string `json:"sha256"`
		} `json:"artifacts"`
	}
	readPinnedNativeJSON(t, "manifest.json", &manifest)
	if manifest.SchemaVersion != 1 || manifest.ContractVersion != pinnedContractVersion || ContractVersion != pinnedContractVersion || manifest.Producer != "terrabase" || manifest.Qualification != "source-artifacts-only" {
		t.Fatalf("unexpected native producer source identity: %+v", manifest)
	}
	expected := map[string]struct{ source, digest string }{
		"client-openapi.json": {"crates/tb-server/assets/client-openapi.json", pinnedOpenAPISHA256},
		"atomic_vectors.json": {"crates/tb-server/tests/support/document/atomic_vectors.json", pinnedAtomicVectorsSHA256},
	}
	if len(manifest.Artifacts) != len(expected) {
		t.Fatal("native producer manifest artifact set changed")
	}
	for _, artifact := range manifest.Artifacts {
		want, ok := expected[artifact.Path]
		if !ok || artifact.ProducerPath != want.source || artifact.SHA256 != want.digest {
			t.Fatalf("unexpected native producer artifact: %+v", artifact)
		}
		raw := readPinnedNativeJSON(t, artifact.Path, &struct{}{})
		sum := sha256.Sum256(raw)
		if hex.EncodeToString(sum[:]) != want.digest {
			t.Fatalf("native producer artifact digest changed: %s", artifact.Path)
		}
		delete(expected, artifact.Path)
	}
	if len(expected) != 0 || ContractOpenAPISHA256 != pinnedOpenAPISHA256 || ContractAtomicVectorsSHA256 != pinnedAtomicVectorsSHA256 {
		t.Fatal("native metadata pins differ from producer artifacts")
	}
}

func TestNativeProducerRoutesAndSchemas(t *testing.T) {
	var spec struct {
		OpenAPI string `json:"openapi"`
		Paths   map[string]map[string]struct {
			OperationID string `json:"operationId"`
			RequestBody struct {
				Content map[string]struct {
					Schema struct {
						Ref string `json:"$ref"`
					} `json:"schema"`
				} `json:"content"`
			} `json:"requestBody"`
			Responses map[string]struct {
				Content map[string]struct {
					Schema struct {
						Ref string `json:"$ref"`
					} `json:"schema"`
				} `json:"content"`
			} `json:"responses"`
		} `json:"paths"`
		Components struct {
			Schemas map[string]struct {
				Required []string `json:"required"`
			} `json:"schemas"`
		} `json:"components"`
	}
	readPinnedNativeJSON(t, "client-openapi.json", &spec)
	if spec.OpenAPI != "3.1.0" {
		t.Fatalf("native OpenAPI version = %q", spec.OpenAPI)
	}
	const prefix = "/v1/tenants/{tenant}/databases/{database}/shards/{shard}"
	const collection = prefix + "/collections/{collection}/documents"
	for _, want := range []ContractOperation{
		{"readDocument", "GET", collection + "/{document}", "", "PointResponse"},
		{"queryDocuments", "POST", collection + "/query", "QueryRequest", "QueryResponse"},
		{"subscribeDocuments", "POST", collection + "/subscriptions", "SubscriptionRequest", "SubscriptionResponse"},
		{"commitDocuments", "POST", prefix + "/transactions", "CommitRequest", "CommitResponse"},
		{"transactionStatus", "POST", prefix + "/transactions/status", "TransactionStatusRequest", "TransactionStatusResponse"},
	} {
		method := "get"
		if want.Method == "POST" {
			method = "post"
		}
		operation, ok := spec.Paths[want.Path][method]
		if !ok || operation.OperationID != want.OperationID {
			t.Fatalf("native producer route mismatch: %+v", want)
		}
		ref := func(name string) string {
			if name == "" {
				return ""
			}
			return "#/components/schemas/" + name
		}
		if operation.RequestBody.Content["application/json"].Schema.Ref != ref(want.RequestModel) || operation.Responses["200"].Content["application/json"].Schema.Ref != ref(want.ResponseModel) {
			t.Fatalf("native producer request/response schema mismatch: %s", want.OperationID)
		}
		if !slices.Contains(ContractOperations, want) || !slices.Contains(ContractRoutes, ContractRoute{want.Method, want.Path, want.OperationID}) {
			t.Fatalf("native metadata omits producer route: %+v", want)
		}
	}
	for name, required := range map[string][]string{
		"PointResponse":             {"version", "state", "state_version", "context", "siblings"},
		"CommitRequest":             {"version", "transaction_id", "writes", "authority", "effect_digest"},
		"CommitResponse":            {"version", "transaction_id", "event", "commit_sequence", "durability", "authority", "effect_digest"},
		"TransactionStatusResponse": {"version", "state", "transaction_id", "authority", "effect_digest", "outcome"},
	} {
		for _, field := range required {
			if !slices.Contains(spec.Components.Schemas[name].Required, field) {
				t.Fatalf("producer schema %s omits required field %s", name, field)
			}
		}
	}
	if len(ContractOperations) != 8 || len(ContractRoutes) != 8 || len(ContractModels) == 0 {
		t.Fatal("native producer metadata is incomplete")
	}
	for _, operation := range ContractOperations {
		if operation.OperationID == "" || operation.Method == "" || operation.Path == "" || strings.HasPrefix(operation.Path, "/api/") {
			t.Fatalf("obsolete or incomplete native metadata: %+v", operation)
		}
	}
}
