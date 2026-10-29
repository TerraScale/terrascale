package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

const managementProjectID = "01M5N9000000000000000000AA"

func TestBackupCommandValidationOffline(t *testing.T) {
	t.Parallel()
	for _, args := range [][]string{
		{"backup", "policy-set", "orders", "none", "36"},
		{"backup", "window", "orders", "invalid"},
		{"backup", "plan", "orders", "invalid"},
		{"backup", "restore-at", "orders", "2026-10-26T00:00:00Z", "restored", "invalid"},
	} {
		if err := runWithInput(t.Context(), args, io.Discard, nil, io.Discard, false); !errors.Is(err, errUsage) {
			t.Errorf("backup parser = %v", err)
		}
	}
}

func managementTerraBase(t *testing.T, handler http.HandlerFunc) *v1.Client {
	t.Helper()
	srv := httptest.NewServer(handler)
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestBackupSettingsSDKRequests(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		command, method, suffix string
		args                    []string
		response                string
	}{
		{"backup policy", "GET", "backup-policy", nil, `{"fullBackupCron":null,"retentionDays":7,"disabled":false}`},
		{"backup policy-set", "PUT", "backup-policy", []string{"0 2 * * *", "14"}, `{"fullBackupCron":"0 2 * * *","retentionDays":14,"disabled":false}`},
		{"backup policy-set", "PUT", "backup-policy", []string{"none", "7"}, `{"fullBackupCron":null,"retentionDays":7,"disabled":false}`},
		{"backup window", "GET", "point-in-time", []string{"customer"}, `{"earliest":"2026-10-20T00:00:00Z","latest":"2026-10-26T00:00:00Z"}`},
		{"backup plan", "POST", "point-in-time/plan", []string{"2026-10-25T12:00:00Z", "customer"}, `{"pointInTime":"2026-10-25T12:00:00Z","historyCheck":"metadata","segmentCount":2}`},
		{"backup restore-at", "POST", "point-in-time/restore", []string{"2026-10-25T12:00:00Z", "restored", "customer"}, `{"id":"01M5N9000000000000000000AB","projectId":"01M5N9000000000000000000AA","name":"restored"}`},
	} {
		t.Run(tc.command+tc.response, func(t *testing.T) {
			t.Parallel()
			c := managementTerraBase(t, func(w http.ResponseWriter, r *http.Request) {
				if r.Method != tc.method || r.URL.Path != "/v1/projects/"+managementProjectID+"/databases/orders/"+tc.suffix {
					t.Errorf("unexpected SDK request %s %s", r.Method, r.URL.Path)
				}
				if tc.method == "PUT" {
					var body map[string]json.RawMessage
					if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
						t.Error(err)
					}
					if _, ok := body["disabled"]; ok || len(body) != 2 || string(body["retentionDays"]) != tc.args[1] {
						t.Errorf("policy shape = %s", body)
					}
				}
				if tc.command == "backup window" && r.URL.Query().Get("destination") != "customer" {
					t.Error("window destination omitted")
				}
				if tc.command == "backup plan" || tc.command == "backup restore-at" {
					var body v1.PointInTimeRequest
					if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Destination != "customer" || body.PointInTime.IsZero() || tc.command == "backup plan" && body.Name != "" || tc.command == "backup restore-at" && body.Name != "restored" {
						t.Errorf("plan request = %+v, %v", body, err)
					}
				}
				io.WriteString(w, tc.response)
			})
			var out bytes.Buffer
			if err := backupSettings(t.Context(), c.Database(managementProjectID, "orders"), tc.command, tc.args, &out); err != nil || !json.Valid(out.Bytes()) {
				t.Fatalf("backup output = %q, %v", out.String(), err)
			}
		})
	}
}

func TestBackupSettingsRejectsInvalidInputBeforeRequest(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(http.ResponseWriter, *http.Request) { t.Error("invalid input sent request") })
	for _, tc := range []struct {
		command string
		args    []string
	}{{"backup policy-set", []string{"* * *", "7"}}, {"backup policy-set", []string{"none", "36"}},
		{"backup plan", []string{"invalid"}}, {"backup window", []string{"unknown"}}} {
		if err := backupSettings(t.Context(), c.Database(managementProjectID, "orders"), tc.command, tc.args, io.Discard); !errors.Is(err, errUsage) {
			t.Errorf("%s error = %v", tc.command, err)
		}
	}
}

func TestBackupSettingsPropagatesAPIAndWriterFailures(t *testing.T) {
	t.Parallel()
	for _, status := range []int{200, 403} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			t.Parallel()
			c := managementTerraBase(t, func(w http.ResponseWriter, _ *http.Request) {
				w.WriteHeader(status)
				io.WriteString(w, `{"fullBackupCron":null,"retentionDays":7,"error":{"code":"forbidden","message":"denied"}}`)
			})
			err := backupSettings(t.Context(), c.Database(managementProjectID, "orders"), "backup policy", nil, managementBrokenWriter{})
			if err == nil || status == 200 && !errors.Is(err, io.ErrClosedPipe) {
				t.Fatalf("backup error = %v", err)
			}
		})
	}
}
