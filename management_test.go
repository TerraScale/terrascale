package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"
)

func TestTerraBaseManagementRoutesAreTypedAndBounded(t *testing.T) {
	var got []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = append(got, r.Method+" "+r.URL.RequestURI())
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/v1/databases":
			if r.Method == http.MethodGet {
				_ = json.NewEncoder(w).Encode(DatabasePage{Databases: []DatabaseRecord{{Name: "prod"}}})
				return
			}
			_ = json.NewEncoder(w).Encode(DatabaseRecord{Name: "prod", Placement: "GRU"})
		case "/api/v1/databases/prod":
			_ = json.NewEncoder(w).Encode(DatabaseRecord{Name: "prod", Placement: "GRU"})
		case "/api/v1/databases/prod/restore":
			_ = json.NewEncoder(w).Encode(DatabaseRecord{Name: "prod", Deleted: false})
		case "/api/v1/databases/prod/collections":
			_ = json.NewEncoder(w).Encode(CollectionPage{Collections: []string{"users"}})
		case "/api/v1/databases/prod/collections/users/schema":
			_ = json.NewEncoder(w).Encode(CollectionSchema{Name: "users"})
		case "/api/v1/databases/prod/branches":
			parent := "prod"
			_ = json.NewEncoder(w).Encode(BranchPage{Branches: []BranchRecord{{Name: "preview", Parent: &parent}}})
		case "/api/v1/databases/prod/backups":
			_ = json.NewEncoder(w).Encode(BackupReadiness{Ready: true, Streaming: true})
		case "/api/v1/databases/prod/backups/restore":
			_ = json.NewEncoder(w).Encode(DatabaseRecord{Name: "restore"})
		case "/api/v1/databases/prod/usage":
			_ = json.NewEncoder(w).Encode(DatabaseUsage{Writes: 3, StorageBytes: 42})
		case "/api/v1/projects/p1/auth/api-keys":
			if r.Method == http.MethodGet {
				_ = json.NewEncoder(w).Encode(APIKeyPage{APIKeys: []APIKey{{KeyID: "key-1", Name: "deploy"}}})
			} else {
				_ = json.NewEncoder(w).Encode(CreatedAPIKey{APIKey: APIKey{KeyID: "key-1"}, Secret: "secret"})
			}
		case "/api/v1/projects/p1/auth/api-keys/key-1/rotate":
			_ = json.NewEncoder(w).Encode(RotatedAPIKey{APIKey: APIKey{KeyID: "key-1"}, Secret: "secret", Rotated: true})
		case "/api/v1/projects/p1/auth/api-keys/key-1":
			_ = json.NewEncoder(w).Encode(RevokedAPIKey{KeyID: "key-1", Revoked: true})
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	c, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	opts := []RequestOption{WithIdempotencyKey("management-test")}

	page, err := c.Databases.ListDatabases(context.Background(), PageRequest{Skip: 2, Take: 10})
	if err != nil || len(page.Databases) != 1 {
		t.Fatalf("ListDatabases: page=%+v err=%v", page, err)
	}
	created, err := c.Databases.CreateDatabase(context.Background(), CreateDatabaseRequest{
		Name: "prod", Placement: []RegionCode{RegionGRU}, Residency: PlacementLockBROnly,
	}, opts...)
	if err != nil || created.Name != "prod" {
		t.Fatalf("CreateDatabase: result=%+v err=%v", created, err)
	}
	if _, err = c.Databases.GetDatabase(context.Background(), "prod"); err != nil {
		t.Fatalf("GetDatabase: %v", err)
	}
	if _, err = c.Databases.DeleteDatabase(context.Background(), "prod", opts...); err != nil {
		t.Fatalf("DeleteDatabase: %v", err)
	}
	if _, err = c.Databases.RestoreDatabase(context.Background(), "prod", opts...); err != nil {
		t.Fatalf("RestoreDatabase: %v", err)
	}
	collections, err := c.Databases.ListCollections(context.Background(), "prod", PageRequest{})
	if err != nil || !reflect.DeepEqual(collections.Collections, []string{"users"}) {
		t.Fatalf("ListCollections: result=%+v err=%v", collections, err)
	}
	if _, err = c.Databases.GetCollectionSchema(context.Background(), "prod", "users"); err != nil {
		t.Fatalf("GetCollectionSchema: %v", err)
	}
	if _, err = c.Databases.PutCollectionSchema(context.Background(), "prod", "users", CollectionSchema{Name: "users"}, opts...); err != nil {
		t.Fatalf("PutCollectionSchema: %v", err)
	}
	if _, err = c.DatabaseBranches.ListBranches(context.Background(), "prod", PageRequest{}); err != nil {
		t.Fatalf("ListBranches: %v", err)
	}
	if _, err = c.DatabaseBranches.CreateBranch(context.Background(), "prod", CreateBranchRequest{Name: "preview"}, opts...); err != nil {
		t.Fatalf("CreateBranch: %v", err)
	}
	if readiness, err := c.Backups.ListDatabaseBackups(context.Background(), "prod"); err != nil || !readiness.Ready {
		t.Fatalf("ListDatabaseBackups: result=%+v err=%v", readiness, err)
	}
	if _, err = c.Backups.RestoreBackup(context.Background(), "prod", RestoreBackupRequest{Name: "restore"}, opts...); err != nil {
		t.Fatalf("RestoreBackup: %v", err)
	}
	usage, err := c.Usage.GetDatabaseUsage(context.Background(), "prod")
	if err != nil || usage.Writes != 3 || usage.StorageBytes != 42 {
		t.Fatalf("GetDatabaseUsage: result=%+v err=%v", usage, err)
	}
	keys, err := c.Access.ListAPIKeys(context.Background(), "p1", "cursor-1", 25)
	if err != nil || len(keys.APIKeys) != 1 {
		t.Fatalf("ListAPIKeys: result=%+v err=%v", keys, err)
	}
	createdKey, err := c.Access.CreateAPIKey(context.Background(), "p1", CreateAPIKeyRequest{Name: "deploy"}, opts...)
	if err != nil || createdKey.Secret != "secret" {
		t.Fatalf("CreateAPIKey: result=%+v err=%v", createdKey, err)
	}
	if _, err = c.Access.RotateAPIKey(context.Background(), "p1", "key-1", opts...); err != nil {
		t.Fatalf("RotateAPIKey: %v", err)
	}
	if _, err = c.Access.RevokeAPIKey(context.Background(), "p1", "key-1", opts...); err != nil {
		t.Fatalf("RevokeAPIKey: %v", err)
	}

	if !reflect.DeepEqual(got[0], "GET /api/v1/databases?skip=2&take=10") {
		t.Fatalf("pagination route = %q", got[0])
	}
}

func TestTerraBaseManagementValidationCancellationAndIdempotency(t *testing.T) {
	c, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:1"})
	if err != nil {
		t.Fatal(err)
	}
	var validation *ValidationError
	if _, err := c.Databases.CreateDatabase(context.Background(), CreateDatabaseRequest{}); !errors.As(err, &validation) {
		t.Fatalf("empty create should return ValidationError, got %T: %v", err, err)
	}
	var idem *IdempotencyError
	if _, err := c.Databases.DeleteDatabase(context.Background(), "prod"); !errors.As(err, &idem) {
		t.Fatalf("missing idempotency key should return IdempotencyError, got %T: %v", err, err)
	}
	var pageErr *PaginationError
	if _, err := c.Databases.ListDatabases(context.Background(), PageRequest{Take: -1}); !errors.As(err, &pageErr) {
		t.Fatalf("invalid page should return PaginationError, got %T: %v", err, err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := c.Databases.ListDatabases(ctx, PageRequest{}); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancelled request should return context.Canceled, got %v", err)
	}
	if err := validatePlacement([]RegionCode{RegionGRU, RegionGRU}, ""); err == nil {
		t.Fatal("duplicate placement should be rejected")
	}
}
