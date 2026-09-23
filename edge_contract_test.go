package sdk

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"slices"
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
	wantRoutes := []EdgeContractRoute{
		{OperationID: "discovery.get", Method: "GET", Path: "/api/v1/discovery", ResponseModel: "DiscoveryResponse"},
		{OperationID: "ingest.publish", Method: "POST", Path: "/api/v1/ingest", ResponseModel: "IngestResponse"},
		{OperationID: "regions.list", Method: "GET", Path: "/api/v1/regions", ResponseModel: "RegionCatalogResponse"},
		{OperationID: "auth.discovery", Method: "GET", Path: "/api/v1/auth/discovery", ResponseModel: "AuthDiscoveryResponse"},
	}
	if !slices.Equal(EdgeContractRoutes, wantRoutes) {
		t.Fatalf("edge routes = %+v", EdgeContractRoutes)
	}
}

func TestMemberAndEdgeRegionsUseDistinctContracts(t *testing.T) {
	var memberResponse string
	for _, operation := range ContractOperations {
		if operation.OperationID == "regions" {
			memberResponse = operation.ResponseModel
		}
	}
	if memberResponse != "HealthResponse" {
		t.Fatalf("member regions response model = %q, want HealthResponse", memberResponse)
	}
	for _, route := range EdgeContractRoutes {
		if route.OperationID == "regions.list" {
			if route.ResponseModel != "RegionCatalogResponse" {
				t.Fatalf("edge regions response model = %q, want RegionCatalogResponse", route.ResponseModel)
			}
			return
		}
	}
	t.Fatal("edge regions route missing")
}
