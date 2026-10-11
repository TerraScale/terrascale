package main

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/oklog/ulid/v2"
	"github.com/oracle/oci-go-sdk/v65/common"
	"github.com/oracle/oci-go-sdk/v65/resourcesearch"
	"github.com/oracle/oci-go-sdk/v65/secrets"
)

// benchSecretName is the OCI Vault secret holding {"api_key":"tsk_…","project":"<ULID>"} for the dedicated benchmark project.
const benchSecretName = "ts-bench-api-key"

const maxSecretBytes = 1 << 10

var (
	apiKeyPattern      = regexp.MustCompile(`^tsk_[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$`)
	errBenchSecretData = fmt.Errorf("OCI Vault secret %s content is invalid", benchSecretName)
)

type searcher interface {
	SearchResources(context.Context, resourcesearch.SearchResourcesRequest) (resourcesearch.SearchResourcesResponse, error)
}

type bundleClient interface {
	GetSecretBundle(context.Context, secrets.GetSecretBundleRequest) (secrets.GetSecretBundleResponse, error)
}

type benchKey struct {
	APIKey  string `json:"api_key"`
	Project string `json:"project"`
}

// benchCredentials stages the Vault key in a 0600 file inside a fresh 0700 dir; cleanup removes the dir.
func benchCredentials(ctx context.Context) (string, func(), error) {
	for _, name := range []string{"OCI_GO_SDK_DEBUG", "OCI_METADATA_BASE_URL", "OCI_SDK_AUTH_CLIENT_REGION_URL", "OCI_DEFAULT_CERTS_PATH", "OCI_DEFAULT_CLIENT_CERTS_PATH", "OCI_DEFAULT_CLIENT_CERTS_PRIVATE_KEY_PATH"} {
		if _, overridden := os.LookupEnv(name); overridden {
			return "", nil, errors.New("the benchmark does not permit OCI debug logging, endpoint or TLS overrides")
		}
	}
	provider := common.DefaultConfigProvider()
	search, err := resourcesearch.NewResourceSearchClientWithConfigurationProvider(provider)
	if err != nil {
		return "", nil, errors.New("cannot create OCI Resource Search client from ~/.oci/config")
	}
	return stageBenchCredentials(ctx, search, newBundles(provider))
}

// newBundles returns a Secrets client per region, bounded to 16 KiB responses.
func newBundles(provider common.ConfigurationProvider) func(string) (bundleClient, error) {
	return func(region string) (bundleClient, error) {
		client, err := secrets.NewSecretsClientWithConfigurationProvider(provider)
		if err != nil {
			return nil, errors.New("cannot create OCI Secrets client from ~/.oci/config")
		}
		client.SetRegion(region)
		dispatcher, ok := client.HTTPClient.(*http.Client)
		if !ok || dispatcher.Transport == nil {
			return nil, errors.New("cannot bound OCI Secrets client responses")
		}
		dispatcher.Transport = responseLimitTransport{transport: dispatcher.Transport, limit: 16 << 10}
		return client, nil
	}
}

func stageBenchCredentials(ctx context.Context, search searcher, bundles func(string) (bundleClient, error)) (string, func(), error) {
	secretID, region, err := findBenchSecret(ctx, search)
	if err != nil {
		return "", nil, err
	}
	client, err := bundles(region)
	if err != nil {
		return "", nil, err
	}
	key, err := readBenchKey(ctx, secretID, client)
	if err != nil {
		return "", nil, err
	}
	body, err := json.Marshal(key)
	if err != nil {
		return "", nil, errBenchSecretData
	}
	defer clear(body)
	dir, err := os.MkdirTemp("", "ts-bench-*")
	if err != nil {
		return "", nil, err
	}
	cleanup := func() { _ = os.RemoveAll(dir) }
	path := filepath.Join(dir, "credentials.json")
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		cleanup()
		return "", nil, err
	}
	_, err = f.Write(body)
	if cerr := f.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		cleanup()
		return "", nil, err
	}
	return path, cleanup, nil
}

// findBenchSecret returns the secret OCID and its region, taken from the OCID's region segment.
func findBenchSecret(ctx context.Context, search searcher) (string, string, error) {
	requestCtx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	retry := common.NoRetryPolicy()
	response, err := search.SearchResources(requestCtx, resourcesearch.SearchResourcesRequest{
		SearchDetails: resourcesearch.StructuredSearchDetails{
			Query: common.String("query vaultsecret resources where displayName = '" + benchSecretName + "' && lifecycleState = 'ACTIVE'"),
		},
		RequestMetadata: common.RequestMetadata{RetryPolicy: &retry},
	})
	if err != nil {
		return "", "", fmt.Errorf("OCI Resource Search failed: %w", err)
	}
	if n := len(response.Items); n == 0 {
		return "", "", fmt.Errorf("OCI Vault secret %s not found", benchSecretName)
	} else if n > 1 {
		return "", "", fmt.Errorf("OCI Vault secret %s is ambiguous", benchSecretName)
	}
	var id string
	if identifier := response.Items[0].Identifier; identifier != nil {
		id = *identifier
	}
	// OCIDs are ocid1.<type>.<realm>.<region>.<unique>.
	parts := strings.Split(id, ".")
	if len(parts) != 5 || parts[3] == "" {
		return "", "", fmt.Errorf("OCI Vault secret %s has an unexpected OCID", benchSecretName)
	}
	return id, parts[3], nil
}

func readBenchKey(ctx context.Context, secretID string, client bundleClient) (benchKey, error) {
	requestCtx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	retry := common.NoRetryPolicy()
	response, err := client.GetSecretBundle(requestCtx, secrets.GetSecretBundleRequest{
		SecretId: common.String(secretID), Stage: secrets.GetSecretBundleStageCurrent,
		RequestMetadata: common.RequestMetadata{RetryPolicy: &retry},
	})
	if err != nil || response.SecretId == nil || *response.SecretId != secretID {
		return benchKey{}, fmt.Errorf("OCI Vault secret %s read failed", benchSecretName)
	}
	content, ok := response.SecretBundleContent.(secrets.Base64SecretBundleContentDetails)
	if !ok {
		pointer, pointerOK := response.SecretBundleContent.(*secrets.Base64SecretBundleContentDetails)
		if !pointerOK || pointer == nil {
			return benchKey{}, errBenchSecretData
		}
		content = *pointer
	}
	if content.Content == nil || len(*content.Content) > base64.StdEncoding.EncodedLen(maxSecretBytes) {
		return benchKey{}, errBenchSecretData
	}
	decoded, err := base64.StdEncoding.Strict().DecodeString(*content.Content)
	defer clear(decoded)
	if err != nil {
		return benchKey{}, errBenchSecretData
	}
	var key benchKey
	if err := json.Unmarshal(decoded, &key); err != nil || !apiKeyPattern.MatchString(key.APIKey) {
		return benchKey{}, errBenchSecretData
	}
	if id, err := ulid.Parse(key.Project); err != nil || id.String() != key.Project {
		return benchKey{}, errBenchSecretData
	}
	return key, nil
}

// responseLimitTransport caps response bodies so a misrouted reply cannot grow memory.
type responseLimitTransport struct {
	transport http.RoundTripper
	limit     int64
}

func (t responseLimitTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	response, err := t.transport.RoundTrip(request)
	if err == nil && response.Body != nil {
		response.Body = struct {
			io.Reader
			io.Closer
		}{io.LimitReader(response.Body, t.limit), response.Body}
	}
	return response, err
}
