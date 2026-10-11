package main

import (
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/oracle/oci-go-sdk/v65/common"
	"github.com/oracle/oci-go-sdk/v65/resourcesearch"
	"github.com/oracle/oci-go-sdk/v65/secrets"
)

const (
	fakeSecretID = "ocid1.vaultsecret.oc1.iad.aaaaaaaa"
	fakeProject  = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
)

var fakeAPIKey = "tsk_0" + strings.Repeat("0", 25) + "_" + strings.Repeat("a", 52)

type fakeSearch struct {
	items []resourcesearch.ResourceSummary
	err   error
}

func (f fakeSearch) SearchResources(context.Context, resourcesearch.SearchResourcesRequest) (resourcesearch.SearchResourcesResponse, error) {
	return resourcesearch.SearchResourcesResponse{ResourceSummaryCollection: resourcesearch.ResourceSummaryCollection{Items: f.items}}, f.err
}

type fakeBundle struct {
	content secrets.SecretBundleContentDetails
}

func (f fakeBundle) GetSecretBundle(context.Context, secrets.GetSecretBundleRequest) (secrets.GetSecretBundleResponse, error) {
	return secrets.GetSecretBundleResponse{SecretBundle: secrets.SecretBundle{SecretId: common.String(fakeSecretID), SecretBundleContent: f.content}}, nil
}

func searchHit(ids ...string) fakeSearch {
	var items []resourcesearch.ResourceSummary
	for _, id := range ids {
		items = append(items, resourcesearch.ResourceSummary{Identifier: common.String(id)})
	}
	return fakeSearch{items: items}
}

func stageFake(search fakeSearch, content secrets.SecretBundleContentDetails) (string, func(), error) {
	bundles := func(string) (bundleClient, error) { return fakeBundle{content: content}, nil }
	return stageBenchCredentials(context.Background(), search, bundles)
}

func encodedContent(payload string) secrets.Base64SecretBundleContentDetails {
	return secrets.Base64SecretBundleContentDetails{Content: common.String(base64.StdEncoding.EncodeToString([]byte(payload)))}
}

func TestStageBenchCredentialsWritesOwnerOnlyFile(t *testing.T) {
	want := fmt.Sprintf(`{"api_key":"%s","project":"%s"}`, fakeAPIKey, fakeProject)
	value := encodedContent(want)
	for name, content := range map[string]secrets.SecretBundleContentDetails{"value": value, "pointer": &value} {
		t.Run(name, func(t *testing.T) {
			path, cleanup, err := stageFake(searchHit(fakeSecretID), content)
			if err != nil {
				t.Fatal(err)
			}
			dir := filepath.Dir(path)
			fi, err := os.Stat(path)
			if err != nil {
				t.Fatal(err)
			}
			if fi.Mode().Perm() != 0o600 {
				t.Fatalf("credentials file mode %o, want 600", fi.Mode().Perm())
			}
			di, err := os.Stat(dir)
			if err != nil {
				t.Fatal(err)
			}
			if di.Mode().Perm() != 0o700 {
				t.Fatalf("credentials dir mode %o, want 700", di.Mode().Perm())
			}
			got, err := os.ReadFile(path)
			if err != nil {
				t.Fatal(err)
			}
			if string(got) != want {
				t.Fatalf("credentials file = %q, want %q", got, want)
			}
			cleanup()
			if _, err := os.Stat(dir); !errors.Is(err, os.ErrNotExist) {
				t.Fatalf("cleanup left %s: %v", dir, err)
			}
		})
	}
}

func TestStageBenchCredentialsRejectsSecretLookup(t *testing.T) {
	errSearch := errors.New("OCI 503")
	cases := map[string]struct {
		search fakeSearch
		want   string
	}{
		"not found":  {search: searchHit(), want: "not found"},
		"ambiguous":  {search: searchHit(fakeSecretID, fakeSecretID), want: "ambiguous"},
		"no region":  {search: searchHit("ocid1.vaultsecret.oc1..aaaaaaaa"), want: "unexpected OCID"},
		"short ocid": {search: searchHit("ocid1.vaultsecret.oc1"), want: "unexpected OCID"},
	}
	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			_, _, err := stageFake(tc.search, encodedContent("{}"))
			if err == nil || !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("error = %v, want containing %q", err, tc.want)
			}
		})
	}
	_, _, err := stageFake(fakeSearch{err: errSearch}, encodedContent("{}"))
	if !errors.Is(err, errSearch) {
		t.Fatalf("search error not wrapped: %v", err)
	}
}

func TestStageBenchCredentialsRejectsInvalidContent(t *testing.T) {
	cases := map[string]secrets.SecretBundleContentDetails{
		"invalid base64": secrets.Base64SecretBundleContentDetails{Content: common.String("!!!")},
		"bad api_key":    encodedContent(`{"api_key":"tsk_bad_SECRETVALUE","project":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}`),
		"lowercase project": encodedContent(fmt.Sprintf(`{"api_key":"%s","project":"%s"}`,
			fakeAPIKey, strings.ToLower(fakeProject))),
		"oversized": secrets.Base64SecretBundleContentDetails{Content: common.String(strings.Repeat("A", 2000))},
	}
	for name, content := range cases {
		t.Run(name, func(t *testing.T) {
			_, _, err := stageFake(searchHit(fakeSecretID), content)
			if !errors.Is(err, errBenchSecretData) {
				t.Fatalf("error = %v, want errBenchSecretData", err)
			}
			for _, secret := range []string{"SECRETVALUE", fakeAPIKey} {
				if strings.Contains(err.Error(), secret) {
					t.Fatalf("error leaks secret content: %v", err)
				}
			}
		})
	}
}
