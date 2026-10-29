package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

const destinationFixture = `{"endpoint":"https://s3.example.test/","region":"region-1","bucket":"own-backups","mode":"both","accessKeyId":"fake-access-id","secretKey":"fake-secret-value"}`

func TestDestinationCommandConsentAndOperationAreCheckedOffline(t *testing.T) {
	t.Parallel()
	for _, args := range [][]string{{"backup", "destination", "set"}, {"backup", "destination", "delete"},
		{"backup", "destination", "invalid"}, {"-yes", "backup", "destination", "get"}} {
		if err := runWithInput(t.Context(), args, io.Discard, forbiddenInput{}, io.Discard, false); err == nil {
			t.Fatalf("unsafe or invalid destination invocation accepted: %v", args)
		}
	}
	if usesDatabaseOperand("backup destination") {
		t.Fatal("project destination treated as a database")
	}
}

func TestBackupDestinationSDKBoundary(t *testing.T) {
	t.Parallel()
	for _, op := range []string{"get", "set", "delete"} {
		t.Run(op, func(t *testing.T) {
			t.Parallel()
			c := managementTerraBase(t, func(w http.ResponseWriter, r *http.Request) {
				method := map[string]string{"get": "GET", "set": "PUT", "delete": "DELETE"}[op]
				if r.Method != method || r.URL.Path != "/v1/projects/"+managementProjectID+"/backup-destination" {
					t.Errorf("unexpected destination SDK request %s %s", r.Method, r.URL.Path)
				}
				if op == "set" {
					var req v1.ProjectBackupDestinationRequest
					if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.AccessKeyID != "fake-access-id" || req.SecretKey != "fake-secret-value" {
						t.Error("stdin credential fields were not sent intact")
					}
				}
				if op == "delete" {
					w.WriteHeader(http.StatusNoContent)
					return
				}
				io.WriteString(w, `{"endpoint":"https://s3.example.test/","region":"region-1","bucket":"own-backups","mode":"both"}`)
			})
			var out bytes.Buffer
			err := backupDestination(t.Context(), c, managementProjectID, op, strings.NewReader(destinationFixture), func(_, _, id string) error {
				if id != managementProjectID {
					t.Error("wrong destination consent target")
				}
				return nil
			}, &out)
			if err != nil || !json.Valid(out.Bytes()) || strings.Contains(out.String(), "fake-") || strings.Contains(out.String(), "secretKey") || strings.Contains(out.String(), "accessKeyId") {
				t.Fatalf("destination output was unsafe or unsuccessful: %v", err)
			}
		})
	}
}

func TestDestinationRejectsMalformedInputWithoutEcho(t *testing.T) {
	t.Parallel()
	for _, value := range []string{`{"secretKey":"sensitive-value"`, destinationFixture + "{}", strings.Replace(destinationFixture, `"mode":"both"`, `"mode":"invalid"`, 1), strings.Repeat("x", (16<<10)+1)} {
		if _, err := readBackupDestination(strings.NewReader(value)); !errors.Is(err, errUsage) || strings.Contains(err.Error(), "sensitive-value") || strings.Contains(err.Error(), "fake-secret-value") {
			t.Fatalf("invalid destination diagnostic = %v", err)
		}
	}
}

func TestDestinationConsentRefusalNeverSendsRequest(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(http.ResponseWriter, *http.Request) { t.Error("mutation after refused consent") })
	refused := errors.New("refused")
	for _, op := range []string{"set", "delete"} {
		if err := backupDestination(t.Context(), c, managementProjectID, op, strings.NewReader(destinationFixture), func(_, _, _ string) error { return refused }, io.Discard); !errors.Is(err, refused) {
			t.Fatalf("destination refusal = %v", err)
		}
	}
}

func TestDestinationServerErrorCannotEchoCredentials(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("X-Request-Id", "request-123")
		w.WriteHeader(http.StatusForbidden)
		io.WriteString(w, `{"error":{"code":"forbidden","message":"fake-secret-value"}}`)
	})
	err := backupDestination(t.Context(), c, managementProjectID, "set", strings.NewReader(destinationFixture), func(_, _, _ string) error { return nil }, io.Discard)
	var api *v1.APIError
	if !errors.As(err, &api) || api.Status != 403 || strings.Contains(err.Error(), "fake-secret-value") {
		t.Fatal("credential-bearing producer message escaped redaction")
	}
}

func TestDestinationWriterFailure(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(w http.ResponseWriter, _ *http.Request) {
		io.WriteString(w, `{"endpoint":"https://s3.example.test/","region":"region-1","bucket":"own-backups","mode":"both"}`)
	})
	if err := backupDestination(t.Context(), c, managementProjectID, "get", nil, nil, managementBrokenWriter{}); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatalf("destination writer failure = %v", err)
	}
}
