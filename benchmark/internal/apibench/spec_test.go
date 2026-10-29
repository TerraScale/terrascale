package apibench

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const workload = "../../workloads/api-bench.json"

func TestLoadSpecAcceptsWorkload(t *testing.T) {
	t.Parallel()
	s, err := LoadSpec(workload)
	if err != nil {
		t.Fatal(err)
	}
	if s.Endpoint != "https://gru.api.terrascale.tech" || len(s.Scenarios) != 8 {
		t.Fatalf("unexpected spec: %+v", s)
	}
}

func TestLoadSpecRejects(t *testing.T) {
	t.Parallel()
	raw, err := os.ReadFile(workload)
	if err != nil {
		t.Fatal(err)
	}
	cases := map[string][2]string{
		"unknown field":              {`"seed_documents": 256`, `"seed_documents": 256, "bogus": 1`},
		"concurrency not increasing": {`[1, 8, 32, 128, 512]`, `[1, 8, 8]`},
		"unknown scenario":           {`{"name": "doc.get"}`, `{"name": "doc.nope"}`},
		"payload below 256":          {`[256, 4096, 65536]`, `[12, 4096, 65536]`},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(string(raw), c[0]) {
				t.Fatalf("fixture missing %q", c[0])
			}
			path := filepath.Join(t.TempDir(), "spec.json")
			if err := os.WriteFile(path, []byte(strings.Replace(string(raw), c[0], c[1], 1)), 0o600); err != nil {
				t.Fatal(err)
			}
			if _, err := LoadSpec(path); err == nil {
				t.Fatal("accepted an invalid spec")
			}
		})
	}
}
