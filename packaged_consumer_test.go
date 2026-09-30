package sdk

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"maps"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

// Build a real Go module archive, then download and use it from an isolated
// file proxy. The consumer has no replace directive or source-checkout access.
func TestPackagedGoConsumerPreservesNativeIntegers(t *testing.T) {
	root := filepath.Dir(filepath.Dir(pinnedNativeContractRoot(t)))
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
	sums, err := os.ReadFile(filepath.Join(root, "go.sum"))
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
		if file.IsDir() || strings.HasSuffix(name, "_test.go") || !(strings.HasSuffix(name, ".go") || name == "go.mod" || name == "go.sum") {
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
	// Derive imported archives and pinned module-graph metadata instead of
	// maintaining a second dependency inventory. Only the parent uses its proxy.
	mirrorPackagedModuleDependencies(t, root, proxy, module, sums)
	write(filepath.Join(consumer, "go.mod"), []byte("module consumer.test\n\ngo 1.25.0\n\nrequire terrascale.tech/sdk v0.0.0\n"))
	write(filepath.Join(consumer, "go.sum"), sums)
	write(filepath.Join(consumer, "consumer_test.go"), []byte(packagedNativeConsumer))
	command := exec.CommandContext(t.Context(), "go", "test", "-mod=mod", "-count=1", "./...")
	command.Dir = consumer
	command.Env = append(os.Environ(), "GOWORK=off", "GOENV=off", "GOFLAGS=-modcacherw", "GOTOOLCHAIN=local", "GOSUMDB=off", "GONOSUMDB=*", "GOPRIVATE=", "GONOPROXY=none", "GOPROXY="+(&url.URL{Scheme: "file", Path: proxy}).String(), "GOPATH="+filepath.Join(workspace, "gopath"), "GOMODCACHE="+filepath.Join(workspace, "modules"), "GOCACHE="+filepath.Join(workspace, "build-cache"))
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("clean packaged consumer failed: %v\n%s", err, output)
	}
	installed := exec.CommandContext(t.Context(), "go", "list", "-mod=readonly", "-m", "-json", "terrascale.tech/sdk")
	installed.Dir, installed.Env = consumer, command.Env
	output, err := installed.Output()
	if err != nil {
		t.Fatalf("inspect installed SDK: %v", err)
	}
	var identity struct {
		Path, Version, Dir string
		Replace            *struct{}
	}
	if err := json.Unmarshal(output, &identity); err != nil {
		t.Fatal(err)
	}
	relative, err := filepath.Rel(filepath.Join(workspace, "modules"), identity.Dir)
	if err != nil || !filepath.IsLocal(relative) || identity.Path != "terrascale.tech/sdk" || identity.Version != "v0.0.0" || identity.Replace != nil {
		t.Fatalf("SDK did not resolve from the isolated module cache: %+v (%v)", identity, err)
	}
}

func mirrorPackagedModuleDependencies(t *testing.T, root, proxy string, module, sums []byte) {
	t.Helper()
	runGo := func(dir string, args ...string) []byte {
		t.Helper()
		command := exec.CommandContext(t.Context(), "go", args...)
		command.Dir = dir
		command.Env = append(os.Environ(), "GOWORK=off", "GOENV=off", "GOFLAGS=", "GOTOOLCHAIN=local")
		var stdout, stderr bytes.Buffer
		command.Stdout, command.Stderr = &stdout, &stderr
		if err := command.Run(); err != nil {
			t.Fatalf("go %s: %v\n%s\n%s", strings.Join(args, " "), err, &stdout, &stderr)
		}
		return stdout.Bytes()
	}
	modules := make(map[string]struct{})
	packages := json.NewDecoder(bytes.NewReader(runGo(root, "list", "-mod=readonly", "-deps", "-json", ".")))
	for {
		var pkg struct {
			Module *struct {
				Path, Version string
				Main          bool
				Replace       *struct{}
			}
		}
		if err := packages.Decode(&pkg); errors.Is(err, io.EOF) {
			break
		} else if err != nil {
			t.Fatal(err)
		}
		if pkg.Module == nil || pkg.Module.Main {
			continue
		}
		if pkg.Module.Replace != nil || pkg.Module.Version == "" {
			t.Fatalf("SDK dependency must be a pinned module without replacement: %+v", pkg.Module)
		}
		modules[pkg.Module.Path+"@"+pkg.Module.Version] = struct{}{}
	}
	if len(modules) == 0 {
		return
	}
	checksums := make(map[string]string)
	for line := range strings.SplitSeq(string(sums), "\n") {
		fields := strings.Fields(line)
		if len(fields) == 3 {
			checksums[fields[0]+" "+fields[1]] = fields[2]
		}
	}
	// Downloading may update metadata, so use an owned copy rather than writing
	// to the SDK checkout. Go verifies downloads against the pinned checksums.
	metadata := t.TempDir()
	for name, data := range map[string][]byte{"go.mod": module, "go.sum": sums} {
		if err := os.WriteFile(filepath.Join(metadata, name), data, 0o600); err != nil {
			t.Fatal(err)
		}
	}
	cache := strings.TrimSpace(string(runGo(root, "env", "GOMODCACHE")))
	mirror := func(source string) {
		t.Helper()
		relative, err := filepath.Rel(filepath.Join(cache, "cache", "download"), source)
		if source == "" || err != nil || !filepath.IsLocal(relative) {
			t.Fatalf("dependency metadata is outside the module cache: %s (%v)", source, err)
		}
		data, err := os.ReadFile(source)
		if err != nil {
			t.Fatal(err)
		}
		destination := filepath.Join(proxy, relative)
		if err := os.MkdirAll(filepath.Dir(destination), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(destination, data, 0o600); err != nil {
			t.Fatal(err)
		}
	}
	// Older dependencies require unpruned recursive graphs, including versions
	// whose packages are not imported. Let Go load that graph in the owned copy,
	// then copy its originally pinned .mod inputs without fetching their ZIPs.
	// Graph endpoints can be pruned: only metadata loaded into the cache and
	// pinned by the original go.sum is copied. The child checks those same pins.
	graphModules := make(map[string]struct{})
	for node := range strings.FieldsSeq(string(runGo(metadata, "mod", "graph"))) {
		path, version, versioned := strings.Cut(node, "@")
		if versioned && checksums[path+" "+version+"/go.mod"] != "" {
			graphModules[node] = struct{}{}
		}
	}
	escape := func(value string) string {
		var escaped strings.Builder
		for _, character := range value {
			if character >= 'A' && character <= 'Z' {
				escaped.WriteByte('!')
				character += 'a' - 'A'
			}
			escaped.WriteRune(character)
		}
		return escaped.String()
	}
	for _, node := range slices.Sorted(maps.Keys(graphModules)) {
		path, version, _ := strings.Cut(node, "@")
		source := filepath.Join(cache, "cache", "download", escape(path), "@v", escape(version)+".mod")
		if _, err := os.Stat(source); errors.Is(err, os.ErrNotExist) {
			continue // Go did not need this pruned endpoint's metadata.
		} else if err != nil {
			t.Fatal(err)
		}
		mirror(source)
	}
	args := append([]string{"mod", "download", "-json"}, slices.Sorted(maps.Keys(modules))...)
	downloads := json.NewDecoder(bytes.NewReader(runGo(metadata, args...)))
	for {
		var download struct {
			Path, Version, Info, GoMod, Zip, Sum, GoModSum, Error string
		}
		if err := downloads.Decode(&download); errors.Is(err, io.EOF) {
			break
		} else if err != nil {
			t.Fatal(err)
		}
		id := download.Path + "@" + download.Version
		if _, expected := modules[id]; !expected || download.Error != "" {
			t.Fatalf("unexpected dependency download: %+v", download)
		}
		if download.Sum == "" || download.GoModSum == "" || checksums[download.Path+" "+download.Version] != download.Sum || checksums[download.Path+" "+download.Version+"/go.mod"] != download.GoModSum {
			t.Fatalf("dependency checksums differ from the SDK's go.sum: %s", id)
		}
		for _, source := range []string{download.Info, download.GoMod, download.Zip} {
			mirror(source)
		}
		delete(modules, id)
	}
	if len(modules) != 0 {
		t.Fatalf("missing dependency downloads: %v", slices.Sorted(maps.Keys(modules)))
	}
}

const packagedNativeConsumer = `package consumer_test

import (
	"encoding/json"
	"math"
	"net/http"
	"net/http/httptest"
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestNativeContractAndInteger(t *testing.T) {
	scope := sdk.DocumentScope{
		Tenant:   "11111111-1111-1111-1111-111111111111",
		Database: "22222222-2222-2222-2222-222222222222",
		Shard:    sdk.DecimalU64(math.MaxUint64),
	}
	collection := sdk.UUID("33333333-3333-3333-3333-333333333333")
	document := sdk.UUID("44444444-4444-4444-4444-444444444444")
	const integer = int64(9007199254740993)
	if err := sdk.ValidateSQL("SELECT value FROM orders WHERE value = :value", sdk.SQLParameters{"value": integer}); err != nil {
		t.Fatal(err)
	}
	encoded, err := json.Marshal(sdk.I64Value(integer))
	if err != nil {
		t.Fatal(err)
	}
	var roundtrip sdk.Value
	if err := json.Unmarshal(encoded, &roundtrip); err != nil {
		t.Fatal(err)
	}
	got, ok := roundtrip.Int64()
	if !ok || got != integer {
		t.Fatalf("integer rounded during JSON roundtrip: %d", got)
	}

	// Literal response bytes independently exercise the installed HTTP decoder.
	const point = "{\"version\":1,\"state\":\"value\",\"state_version\":\"0100000000000000000000000000000000000000000000000000000000000000\"," +
		"\"context\":[{\"replica\":\"33333333-3333-3333-3333-333333333333\",\"incarnation\":\"01000000000000000000000000000000\",\"sequence\":\"18446744073709551615\"}],\"siblings\":[{\"event\":{\"database\":\"22222222-2222-2222-2222-222222222222\",\"shard\":\"18446744073709551615\",\"replica\":\"33333333-3333-3333-3333-333333333333\",\"incarnation\":\"01000000000000000000000000000000\",\"sequence\":\"18446744073709551615\"}," +
		"\"schema_generation\":\"18446744073709551615\",\"schema_digest\":\"0200000000000000000000000000000000000000000000000000000000000000\"," +
		"\"kind\":\"live\",\"fields\":[{\"id\":1,\"value\":{\"type\":\"i64\",\"value\":\"9007199254740993\"}}]}]}"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, request *http.Request) {
		if request.Method != "GET" || request.URL.RawQuery != "" || request.URL.Path != "/v1/tenants/11111111-1111-1111-1111-111111111111/databases/22222222-2222-2222-2222-222222222222/shards/18446744073709551615/collections/33333333-3333-3333-3333-333333333333/documents/44444444-4444-4444-4444-444444444444" {
			t.Errorf("unexpected route: %s %s", request.Method, request.URL)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(point))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{DataBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	result, err := client.Documents.Get(t.Context(), scope, collection, document)
	if err != nil {
		t.Fatal(err)
	}
	if result.State != sdk.PointValue || result.StateVersion == nil || *result.StateVersion != (sdk.Hex32{1}) || len(result.Context) != 1 || len(result.Siblings) != 1 || len(result.Siblings[0].Fields) != 1 {
		t.Fatalf("native point was not retained: %+v", result)
	}
	if result.Context[0].Sequence != sdk.DecimalU64(math.MaxUint64) || result.Siblings[0].Event.Shard != scope.Shard || result.Siblings[0].Event.Sequence != sdk.DecimalU64(math.MaxUint64) || result.Siblings[0].SchemaGeneration != sdk.DecimalU64(math.MaxUint64) {
		t.Fatalf("native u64 metadata was rounded: %+v", result)
	}
	got, ok = result.Siblings[0].Fields[0].Value.Int64()
	if !ok || result.Siblings[0].Fields[0].ID != 1 || got != integer {
		t.Fatalf("integer rounded in native HTTP response: %d", got)
	}
}
`
