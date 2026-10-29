package tsauth_test

import (
	"bytes"
	"encoding/json"
	"os"
	"strings"
	"testing"

	"terrascale.tech/sdk/tsauth"
)

func projectAuthFixture(t *testing.T) []byte {
	t.Helper()
	data, err := os.ReadFile("testdata/project-auth-settings.json")
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestProjectAuthResultContract(t *testing.T) {
	data := projectAuthFixture(t)
	var s tsauth.ProjectAuthSettings
	if err := json.Unmarshal(data, &s); err != nil {
		t.Fatal(err)
	}
	if s.Counts.Users != 9007199254740993 || s.Counts.Clients != 9223372036854775807 || s.Methods.Password || s.Methods.SignUp || len(s.Providers) != 4 {
		t.Fatalf("lost required response values: %+v", s)
	}
	for _, edit := range []func(map[string]any){
		func(m map[string]any) { delete(m, "project_id") },
		func(m map[string]any) { m["project_id"] = "legacy" },
		func(m map[string]any) { m["methods"] = nil },
		func(m map[string]any) { delete(m["methods"].(map[string]any), "sign_up") },
		func(m map[string]any) { m["methods"].(map[string]any)["password"] = nil },
		func(m map[string]any) { delete(m["counts"].(map[string]any), "clients") },
		func(m map[string]any) { m["counts"].(map[string]any)["users"] = -1 },
		func(m map[string]any) { m["providers"] = nil },
		func(m map[string]any) { delete(m["providers"].([]any)[0].(map[string]any), "configured") },
		func(m map[string]any) { m["configuration"].(map[string]any)["redirect_urls"] = nil },
	} {
		var m map[string]any
		decoder := json.NewDecoder(bytes.NewReader(data))
		decoder.UseNumber()
		if err := decoder.Decode(&m); err != nil {
			t.Fatal(err)
		}
		edit(m)
		bad, _ := json.Marshal(m)
		if json.Unmarshal(bad, &s) == nil {
			t.Fatalf("accepted invalid contract: %s", bad)
		}
	}
	var m map[string]json.RawMessage
	_ = json.Unmarshal(data, &m)
	m["branding"], m["configuration"] = json.RawMessage("null"), json.RawMessage("null")
	body, _ := json.Marshal(m)
	if err := json.Unmarshal(body, &s); err != nil || s.Branding != nil || s.Configuration != nil {
		t.Fatalf("nullable sections: %v", err)
	}
}

func TestProjectAuthPatchOmission(t *testing.T) {
	off := false
	b, err := json.Marshal(tsauth.ProjectAuthSettingsPatch{Methods: &tsauth.ProjectAuthMethodsPatch{SignUp: &off}, Branding: &tsauth.ProjectAuthBranding{}})
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]json.RawMessage
	_ = json.Unmarshal(b, &m)
	if string(m["methods"]) != `{"sign_up":false}` || m["configuration"] != nil || !strings.Contains(string(m["branding"]), `"logo_url":null`) {
		t.Fatalf("lost omission/null/false: %s", b)
	}
	b, _ = json.Marshal(tsauth.ProjectAuthConfiguration{RedirectURLs: []string{}})
	if string(b) != `{"redirect_urls":[]}` {
		t.Fatalf("lost server session default: %s", b)
	}
	b, _ = json.Marshal(tsauth.ProjectAuthSettingsPatch{})
	if string(b) != "{}" {
		t.Fatalf("empty patch: %s", b)
	}
}

func TestProjectAuthContractProvenance(t *testing.T) {
	data, err := os.ReadFile("testdata/project-auth-contract.json")
	if err != nil {
		t.Fatal(err)
	}
	var c struct {
		Producer      string
		CaptureSHA256 string `json:"capture_sha256"`
		Operations    []struct {
			Method, Path, Role string
			ID                 string `json:"operation_id"`
		}
	}
	if err = json.Unmarshal(data, &c); err != nil {
		t.Fatal(err)
	}
	if c.Producer != "1d823d8534fc5035859a47e263691d0c7cb7b744" || c.CaptureSHA256 != "9169acfa9f683dd6b861eb43e430c6f87a4dd09fb5251f00efefdb2f5f6c466c" || len(c.Operations) != 4 {
		t.Fatal("lost pinned producer/generated-route provenance")
	}
	for _, op := range c.Operations {
		if op.Role == "" || op.ID == "" || !strings.HasPrefix(op.Path, "/v1/{project}/") || (op.Method != "GET" && op.Method != "PUT" && op.Method != "DELETE") {
			t.Fatalf("invalid parity row: %+v", op)
		}
	}
}
