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
func TestPackagedGoConsumerPreservesIntegers(t *testing.T) {
	root := "."
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
	err = filepath.WalkDir(root, func(path string, file os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if file.IsDir() {
			if strings.HasPrefix(file.Name(), ".") && path != root {
				return filepath.SkipDir
			}
			return nil
		}
		name, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		if strings.HasSuffix(name, "_test.go") || !(strings.HasSuffix(name, ".go") || name == "go.mod" || name == "go.sum") {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		entry, err := archive.Create("terrascale.tech/sdk@v0.0.0/" + filepath.ToSlash(name))
		if err != nil {
			return err
		}
		_, err = entry.Write(data)
		return err
	})
	if err != nil {
		t.Fatal(err)
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
	write(filepath.Join(consumer, "consumer_test.go"), []byte(packagedConsumer))
	// The module cache is isolated so the SDK must resolve from the packed
	// archive. The content-addressed build cache is shared; -trimpath keeps its
	// keys independent of this temporary workspace, so unchanged packages are
	// not recompiled on every run.
	command := exec.CommandContext(t.Context(), "go", "test", "-mod=mod", "-count=1", "./...")
	command.Dir = consumer
	command.Env = append(os.Environ(), "GOWORK=off", "GOENV=off", "GOFLAGS=-modcacherw -trimpath", "GOTOOLCHAIN=local", "GOSUMDB=off", "GONOSUMDB=*", "GOPRIVATE=", "GONOPROXY=none", "GOPROXY="+(&url.URL{Scheme: "file", Path: proxy}).String(), "GOPATH="+filepath.Join(workspace, "gopath"), "GOMODCACHE="+filepath.Join(workspace, "modules"))
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

const packagedConsumer = `package consumer_test

import (
	"testing"

	sdk "terrascale.tech/sdk"
	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

var _ v1.TokenSource = (*tsauth.TokenSource)(nil)

func TestInteger(t *testing.T) {
	const integer = int64(9007199254740993)
	if err := sdk.ValidateSQL("SELECT value FROM orders WHERE value = :value", sdk.SQLParameters{"value": integer}); err != nil {
		t.Fatal(err)
	}
	c, err := v1.New(v1.Config{Token: "fixture.jwt.signature"})
	if err != nil || c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main").Name() != "main" {
		t.Fatalf("packaged v1 client: %v", err)
	}
}
`
