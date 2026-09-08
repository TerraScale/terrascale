package sdk

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestEdgeContractPinAndRouteManifest(t *testing.T) {
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime.Caller failed")
	}
	root := filepath.Join(filepath.Dir(file), "contracts", "edge", EdgeContractVersion)
	manifestRaw, err := os.ReadFile(filepath.Join(root, "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	var manifest struct {
		Origin    string `json:"origin"`
		Artifacts []struct {
			Path   string `json:"path"`
			SHA256 string `json:"sha256"`
		} `json:"artifacts"`
	}
	if err := json.Unmarshal(manifestRaw, &manifest); err != nil {
		t.Fatal(err)
	}
	if manifest.Origin != "edge" {
		t.Fatalf("edge manifest origin = %q", manifest.Origin)
	}
	var openapiDigest string
	for _, artifact := range manifest.Artifacts {
		if artifact.Path != "openapi.json" {
			continue
		}
		openapiDigest = artifact.SHA256
	}
	if openapiDigest == "" {
		t.Fatal("edge manifest has no OpenAPI digest")
	}
	openapi, err := os.ReadFile(filepath.Join(root, "openapi.json"))
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(openapi)
	if got := hex.EncodeToString(digest[:]); got != openapiDigest || got != EdgeContractOpenAPISHA256 {
		t.Fatalf("edge OpenAPI SHA-256 = %s, manifest=%s generated=%s", got, openapiDigest, EdgeContractOpenAPISHA256)
	}
	if len(EdgeContractRoutes) != 1 || EdgeContractRoutes[0] != (EdgeContractRoute{OperationID: "regions.list", Method: "GET", Path: "/api/v1/regions"}) {
		t.Fatalf("edge routes = %+v", EdgeContractRoutes)
	}
}
