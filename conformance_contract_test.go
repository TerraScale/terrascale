package sdk

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

type pinnedCorpus struct {
	Protocol       string `json:"protocol"`
	Version        string `json:"version"`
	ManifestSHA256 string `json:"manifestSha256"`
	Vectors        []struct {
		Name string `json:"name"`
		Hex  string `json:"hex"`
	} `json:"vectors"`
}

type hostileVector struct {
	Name   string `json:"name"`
	Code   string `json:"code"`
	Hex    string `json:"hex"`
	Repeat *struct {
		Byte  byte `json:"byte"`
		Count int  `json:"count"`
	} `json:"repeat"`
}

func pinnedContractRoot(t *testing.T) string {
	t.Helper()
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime.Caller failed")
	}
	return filepath.Join(filepath.Dir(file), "contracts", pinnedContractVersion)
}

func readPinnedJSON(t *testing.T, relative string, dst any) []byte {
	t.Helper()
	path := filepath.Join(pinnedContractRoot(t), relative)
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read pinned contract %s: %v", relative, err)
	}
	if err := json.Unmarshal(raw, dst); err != nil {
		t.Fatalf("parse pinned contract %s: %v", relative, err)
	}
	return raw
}

func TestGeneratedContractProvenance(t *testing.T) {
	var manifest struct {
		SchemaVersion   int    `json:"schemaVersion"`
		ContractVersion string `json:"contractVersion"`
		Artifacts       []struct {
			Path   string `json:"path"`
			BLAKE3 string `json:"blake3"`
		} `json:"artifacts"`
	}
	readPinnedJSON(t, "manifest.json", &manifest)
	if manifest.SchemaVersion != 1 || manifest.ContractVersion != pinnedContractVersion {
		t.Fatalf("manifest identity = schema %d, version %q", manifest.SchemaVersion, manifest.ContractVersion)
	}
	seen := map[string]string{}
	for _, artifact := range manifest.Artifacts {
		seen[artifact.Path] = artifact.BLAKE3
		if _, err := os.Stat(filepath.Join(pinnedContractRoot(t), artifact.Path)); err != nil {
			t.Fatalf("manifest artifact %s is unavailable: %v", artifact.Path, err)
		}
	}
	if seen["openapi.json"] != pinnedOpenAPIBLAKE3 {
		t.Fatal("generated OpenAPI provenance does not match the pinned manifest")
	}
	if seen["msgpack/terrascale.sync.v1.msgpack.json"] != pinnedMessagePackBLAKE3 {
		t.Fatal("generated MessagePack provenance does not match the pinned manifest")
	}
	if ContractVersion != pinnedContractVersion {
		t.Fatalf("generated metadata version = %q, want %q", ContractVersion, pinnedContractVersion)
	}
}

func TestGeneratedContractMetadata(t *testing.T) {
	if len(ContractOperations) == 0 || len(ContractRoutes) == 0 {
		t.Fatal("generated contract operation and route metadata is empty")
	}
	if len(ContractOperations) != len(ContractRoutes) {
		t.Fatalf("generated operation count = %d, route count = %d", len(ContractOperations), len(ContractRoutes))
	}
	if len(ContractModels) == 0 {
		t.Fatal("generated OpenAPI model metadata is empty")
	}
	if len(ContractFrames) < 11 {
		t.Fatalf("generated MessagePack frame metadata has %d messages", len(ContractFrames))
	}
	for _, operation := range ContractOperations {
		if operation.OperationID == "" || operation.Method == "" || operation.Path == "" {
			t.Fatalf("generated operation is incomplete: %+v", operation)
		}
	}
	for _, frame := range ContractFrames {
		if frame.Name == "" || frame.Tag == 0 {
			t.Fatalf("generated frame is not tagged: %+v", frame)
		}
	}
}

func TestP2BConformancePinnedOpenAPI(t *testing.T) {
	var spec struct {
		OpenAPI string                                `json:"openapi"`
		Paths   map[string]map[string]json.RawMessage `json:"paths"`
	}
	readPinnedJSON(t, "openapi.json", &spec)
	if spec.OpenAPI != "3.1.0" {
		t.Fatalf("OpenAPI version = %q, want 3.1.0", spec.OpenAPI)
	}
	for _, path := range []string{
		"/api/v1/databases/{name}/collections/{collection}/documents",
		"/api/v1/databases/{name}/sql",
		"/api/v1/databases/{name}/reactive/poll",
		"/api/v1/regions",
	} {
		if len(spec.Paths[path]) == 0 {
			t.Errorf("pinned OpenAPI is missing %s", path)
		}
	}
}

func TestP2BConformanceCanonicalHTTPParity(t *testing.T) {
	var spec struct {
		Paths map[string]map[string]json.RawMessage `json:"paths"`
	}
	readPinnedJSON(t, "openapi.json", &spec)
	assertOperation := func(path, method, operationID, requestType, responseType, responseMedia, responseStatus string) {
		t.Helper()
		raw, ok := spec.Paths[path][method]
		if !ok {
			t.Fatalf("canonical %s %s operation is missing", method, path)
		}
		var operation struct {
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
		}
		if err := json.Unmarshal(raw, &operation); err != nil {
			t.Fatalf("parse %s %s operation: %v", method, path, err)
		}
		if operation.OperationID != operationID {
			t.Fatalf("%s %s operationId = %q, want %q", method, path, operation.OperationID, operationID)
		}
		if operation.RequestBody.Content["application/json"].Schema.Ref != requestType {
			t.Fatalf("%s %s request schema mismatch", method, path)
		}
		if responseStatus == "" {
			if _, ok := operation.Responses["200"]; ok {
				t.Fatalf("%s %s unexpectedly advertises a successful response", method, path)
			}
			return
		}
		if operation.Responses[responseStatus].Content[responseMedia].Schema.Ref != responseType {
			t.Fatalf("%s %s response schema mismatch", method, path)
		}
	}
	assertOperation(
		"/api/v1/databases/{name}/sql", "post", "execute_sql",
		"#/components/schemas/SqlRequest", "#/components/schemas/SqlResultResponse", "application/json", "200",
	)
	assertOperation(
		"/api/v1/databases/{name}/collections/{collection}/documents/batch", "post", "batch_documents",
		"#/components/schemas/BatchRequest", "#/components/schemas/BatchResponse", "application/json", "200",
	)
	assertOperation(
		"/api/v1/databases/{name}/reactive/poll", "post", "poll",
		"#/components/schemas/ReactivePollRequest", "", "", "",
	)
}

func TestP2BConformanceMessagePackCorpus(t *testing.T) {
	manifestRaw := readPinnedJSON(t, "msgpack/terrascale.sync.v1.msgpack.json", &struct{}{})
	manifestDigest := sha256.Sum256(manifestRaw)
	var corpus pinnedCorpus
	corpusRaw := readPinnedJSON(t, "msgpack/message-vectors.json", &corpus)
	if corpus.Protocol != "terrascale.sync.v1.msgpack" || corpus.Version != "1.0" {
		t.Fatalf("corpus identity = %s %s", corpus.Protocol, corpus.Version)
	}
	if corpus.ManifestSHA256 != hex.EncodeToString(manifestDigest[:]) {
		t.Fatalf("corpus pins manifest %s, got %x", corpus.ManifestSHA256, manifestDigest)
	}
	if got := sha256.Sum256(corpusRaw); hex.EncodeToString(got[:]) != pinnedMessageVectorSHA256 {
		t.Fatalf("message corpus digest changed")
	}
	if len(corpus.Vectors) < 11 {
		t.Fatalf("corpus has %d vectors; expected every published message tag", len(corpus.Vectors))
	}
	for _, vector := range corpus.Vectors {
		wire, err := hex.DecodeString(vector.Hex)
		if err != nil {
			t.Fatalf("vector %q is not hex: %v", vector.Name, err)
		}
		// Client-originating messages are valid corpus entries but cannot be
		// accepted as HTTP server responses by the response decoder.
		if vector.Name == "subscribe" || vector.Name == "unsubscribe" || vector.Name == "checkpoint_ack" {
			continue
		}
		if err := scanReactiveMessagePack(wire); err != nil {
			t.Fatalf("vector %q rejected by scanner: %v", vector.Name, err)
		}
		// The corpus deliberately contains independent view generations; zero
		// means decode the wire value without imposing an attached-view value.
		if _, _, _, _, err := decodeReactiveMessagePack(wire, "fixture", 0); err != nil {
			t.Fatalf("vector %q rejected by SDK decoder: %v", vector.Name, err)
		}
	}
}

func TestP2BConformanceHostileMessagePackCorpus(t *testing.T) {
	var corpus struct {
		Vectors []hostileVector `json:"vectors"`
	}
	raw := readPinnedJSON(t, "msgpack/hostile-vectors.json", &corpus)
	if got := sha256.Sum256(raw); hex.EncodeToString(got[:]) != pinnedHostileVectorSHA256 {
		t.Fatalf("hostile corpus digest changed")
	}
	if len(corpus.Vectors) < 10 {
		t.Fatalf("hostile corpus has only %d vectors", len(corpus.Vectors))
	}
	for _, vector := range corpus.Vectors {
		var wire []byte
		if vector.Repeat != nil {
			wire = make([]byte, vector.Repeat.Count)
			for i := range wire {
				wire[i] = vector.Repeat.Byte
			}
		} else {
			var err error
			wire, err = hex.DecodeString(vector.Hex)
			if err != nil {
				t.Fatalf("hostile vector %q is not hex: %v", vector.Name, err)
			}
		}
		if _, _, _, _, err := decodeReactiveMessagePack(wire, "fixture", 1); err == nil {
			t.Errorf("hostile vector %q (%s) was accepted", vector.Name, vector.Code)
		} else if !hostileErrorMatches(vector.Code, err.Error()) {
			t.Errorf("hostile vector %q (%s) produced unexpected error: %v", vector.Name, vector.Code, err)
		}
	}
}

func hostileErrorMatches(code, message string) bool {
	needles := map[string]string{
		"limit_message":       "exceeds",
		"limit_string":        "exceeds",
		"limit_binary":        "binary exceeds",
		"limit_array":         "array exceeds",
		"map_forbidden":       "maps are forbidden",
		"extension_forbidden": "extensions are forbidden",
		"float32_forbidden":   "floats are forbidden",
		"depth_exceeded":      "nesting exceeds",
		"truncated":           "truncated",
		"trailing_bytes":      "trailing bytes",
		"invalid_type":        "invalid",
	}
	if needle, ok := needles[code]; ok {
		return strings.Contains(message, needle)
	}
	return true
}
