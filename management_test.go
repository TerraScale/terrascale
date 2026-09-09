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
			w.WriteHeader(http.StatusCreated)
			_ = json.NewEncoder(w).Encode(DatabaseRecord{Name: "prod", Placement: "GRU"})
		case "/api/v1/databases/prod":
			if r.Method == http.MethodDelete && r.Header.Get("X-TerraScale-Delete-Confirmation") != "prod" {
				t.Errorf("delete confirmation = %q", r.Header.Get("X-TerraScale-Delete-Confirmation"))
			}
			if r.Method == http.MethodDelete {
				w.WriteHeader(http.StatusAccepted)
				_ = json.NewEncoder(w).Encode(deletionTestOperation("prod", "management-test", "preparing"))
			} else {
				_ = json.NewEncoder(w).Encode(DatabaseRecord{Name: "prod", Placement: "GRU"})
			}
		case "/api/v1/databases/prod/restore":
			_ = json.NewEncoder(w).Encode(DatabaseRecord{Name: "prod", Deleted: false})
		case "/api/v1/databases/prod/collections":
			_ = json.NewEncoder(w).Encode(CollectionPage{Collections: []string{"users"}})
		case "/api/v1/databases/prod/collections/users/schema":
			_ = json.NewEncoder(w).Encode(SchemaDocument{Schema: CollectionSchema{Name: "users", Fields: []FieldDef{}, HistoryRetentionDays: 7, MergePolicy: MergeLastWriteWins}})
		case "/api/v1/databases/prod/branches":
			if r.Method == http.MethodPost {
				w.WriteHeader(http.StatusAccepted)
				_, _ = w.Write([]byte(`{"environment":"production","tenant_id":"tenant-1","project_id":"project-1","operation_id":"management-test","database_id":"db-preview","parent_database_id":"prod","key_version":"key-1","name":"preview","regions":["GRU"],"residency":"","state":"pending"}`))
			} else {
				parent := "prod"
				_ = json.NewEncoder(w).Encode(BranchPage{Branches: []BranchRecord{{Name: "preview", Parent: &parent}}})
			}
		case "/api/v1/databases/prod/backups":
			_ = json.NewEncoder(w).Encode(BackupReadiness{Ready: true, Streaming: true})
		case "/api/v1/databases/prod/backups/restore":
			var request RestoreBackupRequest
			if err := json.NewDecoder(r.Body).Decode(&request); err != nil || request.OperationID != "management-test" {
				t.Errorf("restore request identity = %+v, error = %v", request, err)
			}
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(`{"environment":"production","tenant_id":"tenant-1","project_id":"project-1","operation_id":"management-test","database_id":"db-restore","parent_database_id":"prod","key_version":"key-1","name":"restore","regions":["GRU"],"residency":"","state":"pending"}`))
		case "/api/v1/databases/prod/usage":
			_ = json.NewEncoder(w).Encode(DatabaseUsage{DatabaseID: "prod", Meters: []MeterTotal{{Meter: MeterDocumentWriteUnits, Units: "3"}, {Meter: MeterStorageByteSeconds, Units: "42"}}})
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
		Name: "prod", Placement: []RegionCode{RegionGRU}, Residency: PlacementLockBROnly, OperationID: "create-prod",
	}, opts...)
	if err != nil || created.Database == nil || created.Database.Name != "prod" {
		t.Fatalf("CreateDatabase: result=%+v err=%v", created, err)
	}
	if _, err = c.Databases.GetDatabase(context.Background(), "prod"); err != nil {
		t.Fatalf("GetDatabase: %v", err)
	}
	if _, err = c.Databases.DeleteDatabase(context.Background(), "prod", append(opts, WithDeleteConfirmation("prod"))...); err != nil {
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
	if _, err = c.Databases.PutCollectionSchema(context.Background(), "prod", "users", CollectionSchema{Name: "users", Fields: []FieldDef{}, HistoryRetentionDays: 7, MergePolicy: MergeLastWriteWins}, opts...); err != nil {
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
	if usage, err := c.Usage.GetDatabaseUsage(context.Background(), "prod"); err != nil || usage.DatabaseID != "prod" || usage.Meters[0].Units != "3" {
		t.Fatalf("GetDatabaseUsage: result=%+v err=%v", usage, err)
	}
	if !reflect.DeepEqual(got[0], "GET /api/v1/databases") {
		t.Fatalf("pagination route = %q", got[0])
	}
}

func TestRegionsListDecodesEdgeCatalogAndRejectsMemberHealth(t *testing.T) {
	t.Run("edge catalog", func(t *testing.T) {
		server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path != "/api/v1/regions" || r.Method != http.MethodGet {
				t.Fatalf("request = %s %s", r.Method, r.URL)
			}
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.43,"longitude":-46.47,"available":true},{"code":"IAD","name":"Ashburn","country":"US","latitude":39.04,"longitude":-77.49,"available":false}],"hosts":{"GRU":"gru.api.terrascale.tech","IAD":"iad.api.terrascale.tech"}}`))
		}))
		defer server.Close()
		client, err := NewClient(Config{APIBaseURL: server.URL})
		if err != nil {
			t.Fatal(err)
		}
		regions, err := client.Regions.List(t.Context())
		if err != nil {
			t.Fatal(err)
		}
		if len(regions.Regions) != 2 || regions.Regions[0].Code != RegionGRU || regions.Regions[0].Name != "Sao Paulo" || regions.Regions[0].Country != "BR" || !regions.Regions[0].Available || regions.Regions[1].Code != "IAD" || regions.Regions[1].Available {
			t.Fatalf("regions = %+v", regions)
		}
		if regions.Hosts["GRU"] != "gru.api.terrascale.tech" || regions.Hosts["IAD"] != "iad.api.terrascale.tech" {
			t.Fatalf("hosts = %+v", regions.Hosts)
		}
	})

	t.Run("member health is not a catalog", func(t *testing.T) {
		server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{"region":"SFO","healthy":false}`))
		}))
		defer server.Close()
		client, err := NewClient(Config{APIBaseURL: server.URL})
		if err != nil {
			t.Fatal(err)
		}
		regions, err := client.Regions.List(t.Context())
		if regions != nil || err == nil {
			t.Fatalf("member health accepted as catalog: regions=%+v err=%v", regions, err)
		}
	})

	for _, test := range []struct {
		name string
		body string
		want int
		fail bool
	}{
		{name: "explicit empty catalog", body: `{"regions":[],"hosts":{}}`, fail: true},
		{name: "missing regions", body: `{"hosts":{}}`, fail: true},
		{name: "null regions", body: `{"regions":null,"hosts":{}}`, fail: true},
		{name: "invalid code shape", body: `{"regions":[{"code":"gru","name":"Sao Paulo","country":"BR","latitude":0,"longitude":0,"available":true}],"hosts":{}}`, fail: true},
		{name: "invalid leading hyphen", body: `{"regions":[{"code":"-GRU","name":"Sao Paulo","country":"BR","latitude":0,"longitude":0,"available":true}],"hosts":{}}`, fail: true},
		{name: "duplicate region code", body: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":0,"longitude":0,"available":true},{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":0,"longitude":0,"available":true}],"hosts":{}}`, fail: true},
		{name: "invalid country shape", body: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BRA","latitude":0,"longitude":0,"available":true}],"hosts":{}}`, fail: true},
		{name: "invalid coordinates", body: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":91,"longitude":0,"available":true}],"hosts":{}}`, fail: true},
		{name: "missing required region field", body: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":0,"longitude":0}],"hosts":{}}`, fail: true},
		{name: "missing hosts", body: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":0,"longitude":0,"available":true}]}`, fail: true},
		{name: "null host value", body: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":0,"longitude":0,"available":true}],"hosts":{"GRU":null}}`, fail: true},
	} {
		t.Run(test.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				_, _ = w.Write([]byte(test.body))
			}))
			defer server.Close()
			client, err := NewClient(Config{APIBaseURL: server.URL})
			if err != nil {
				t.Fatal(err)
			}
			regions, err := client.Regions.List(t.Context())
			if test.fail {
				if err == nil || regions != nil {
					t.Fatalf("malformed catalog accepted: regions=%+v err=%v", regions, err)
				}
				return
			}
			if err != nil || len(regions.Regions) != test.want {
				t.Fatalf("regions=%+v err=%v", regions, err)
			}
		})
	}
}

func TestRegionJSONIncludesSchemaRequiredFields(t *testing.T) {
	data, err := json.Marshal(Region{})
	if err != nil {
		t.Fatal(err)
	}
	if got, want := string(data), `{"code":"","name":"","country":"","latitude":0,"longitude":0,"available":false}`; got != want {
		t.Fatalf("encoded region = %s, want %s", got, want)
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
	var confirmation *DeleteConfirmationError
	if _, err := c.Databases.DeleteDatabase(context.Background(), "prod", WithIdempotencyKey("delete-1")); !errors.As(err, &confirmation) {
		t.Fatalf("missing delete confirmation should return DeleteConfirmationError, got %T: %v", err, err)
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

func TestPlacementLockNormalizationAndBounds(t *testing.T) {
	lock, err := normalizePlacementLock(PlacementLock(" gru, FRA,gru "))
	if err != nil || lock != PlacementLock("GRU,FRA") {
		t.Fatalf("normalized lock = %q, err=%v", lock, err)
	}
	if err := validatePlacement([]RegionCode{RegionFRA}, PlacementLockBROnly); err == nil {
		t.Fatal("BR-only lock admitted FRA")
	}
	if err := validatePlacement([]RegionCode{RegionGRU}, PlacementLockEUOnly); err == nil {
		t.Fatal("EU-only lock admitted GRU")
	}
	var validation *ValidationError
	if err := validateDatabaseName("Upper"); !errors.As(err, &validation) {
		t.Fatalf("uppercase database name error = %T: %v", err, err)
	}
}
