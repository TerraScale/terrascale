package sdk

import (
	"archive/zip"
	"bytes"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

// Build a real Go module archive, then download and use it from an isolated
// file proxy. The consumer has no replace directive or source-checkout access.
func TestPackagedGoConsumerPreservesSQLIntegers(t *testing.T) {
	root := filepath.Dir(filepath.Dir(pinnedContractRoot(t)))
	workspace := t.TempDir()
	proxy := filepath.Join(workspace, "proxy")
	versionRoot := filepath.Join(proxy, "terrascale.tech", "sdk", "@v")
	consumer := filepath.Join(workspace, "consumer")
	for _, dir := range []string{versionRoot, consumer} {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	write := func(path string, data []byte) {
		if err := os.WriteFile(path, data, 0o600); err != nil {
			t.Fatal(err)
		}
	}
	module, err := os.ReadFile(filepath.Join(root, "go.mod"))
	if err != nil {
		t.Fatal(err)
	}
	write(filepath.Join(versionRoot, "v0.0.0.mod"), module)
	write(filepath.Join(versionRoot, "v0.0.0.info"), []byte(`{"Version":"v0.0.0","Time":"2026-01-01T00:00:00Z"}`))
	var packed bytes.Buffer
	archive := zip.NewWriter(&packed)
	files, err := os.ReadDir(root)
	if err != nil {
		t.Fatal(err)
	}
	for _, file := range files {
		name := file.Name()
		if file.IsDir() || strings.HasSuffix(name, "_test.go") || !(strings.HasSuffix(name, ".go") || name == "go.mod") {
			continue
		}
		data, err := os.ReadFile(filepath.Join(root, name))
		if err != nil {
			t.Fatal(err)
		}
		entry, err := archive.Create("terrascale.tech/sdk@v0.0.0/" + name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := entry.Write(data); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	write(filepath.Join(versionRoot, "v0.0.0.zip"), packed.Bytes())
	write(filepath.Join(consumer, "go.mod"), []byte("module consumer.test\n\ngo 1.24.0\n\nrequire terrascale.tech/sdk v0.0.0\n"))
	write(filepath.Join(consumer, "consumer_test.go"), []byte(packagedSQLConsumer))
	command := exec.CommandContext(t.Context(), "go", "test", "-count=1", "./...")
	command.Dir = consumer
	command.Env = append(os.Environ(), "GOWORK=off", "GOENV=off", "GOFLAGS=-mod=mod -modcacherw", "GOTOOLCHAIN=local", "GOSUMDB=off", "GOPRIVATE=", "GONOPROXY=none", "GOPROXY="+(&url.URL{Scheme: "file", Path: proxy}).String(), "GOPATH="+filepath.Join(workspace, "gopath"), "GOMODCACHE="+filepath.Join(workspace, "modules"))
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("clean packaged consumer failed: %v\n%s", err, output)
	}
}

const packagedSQLConsumer = `package consumer_test
import (
 "encoding/json"
 "net/http"
 "net/http/httptest"
 "testing"
 sdk "terrascale.tech/sdk"
)
func TestSQLContractAndInteger(t *testing.T) {
 const integer = int64(9007199254740993)
 server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, request *http.Request) {
  if request.Method != "POST" || request.URL.Path != "/api/v1/databases/demo/sql" { t.Errorf("unexpected route: %s %s", request.Method, request.URL) }
  var body struct { SQL string; Parameters map[string]int64 }
  if err := json.NewDecoder(request.Body).Decode(&body); err != nil { t.Error(err) }
  if body.SQL != "SELECT value FROM orders WHERE value = :value" || body.Parameters["value"] != integer { t.Errorf("incorrect SQL body: %+v", body) }
  w.Header().Set("Content-Type", "application/json")
  _, _ = w.Write([]byte("{\"matched\":1,\"rows\":[{\"id\":\"order-1\",\"value\":{\"value\":9007199254740993}}]}"))
 }))
 defer server.Close()
 client, err := sdk.NewClient(sdk.Config{APIBaseURL:server.URL})
 if err != nil { t.Fatal(err) }
 result, err := client.ExecuteSQL(t.Context(), "demo", "SELECT value FROM orders WHERE value = :value", sdk.SQLParameters{"value":integer})
 if err != nil { t.Fatal(err) }
 if result.Matched != 1 || len(result.Rows) != 1 { t.Fatalf("wrong rows: %+v", result) }
 value, ok := result.Rows[0].Value.(map[string]any)["value"].(json.Number)
 if !ok { t.Fatalf("projected integer lost its exact representation: %#v", result.Rows[0].Value) }
 got, err := value.Int64()
 if err != nil || got != integer { t.Fatalf("integer rounded: %s: %v", value, err) }
}
`
