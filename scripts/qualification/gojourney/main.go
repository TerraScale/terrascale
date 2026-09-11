// Command gojourney exercises the published TerraScale Go SDK against a real
// public API. It intentionally has no mock or checkout replacement path.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	sdk "terrascale.tech/sdk"
)

type observation struct {
	Phase  string `json:"phase"`
	Action string `json:"action"`
	OK     bool   `json:"ok"`
	Detail string `json:"detail,omitempty"`
}

type config struct {
	apiURL, project, database, key, runID, collection string
	denyKey                                           string
}

func getenv(name string) (string, error) {
	v := strings.TrimSpace(os.Getenv(name))
	if v == "" {
		return "", fmt.Errorf("required environment variable %s is missing", name)
	}
	return v, nil
}

func loadConfig() (config, error) {
	var c config
	var err error
	if c.apiURL, err = getenv("TERRASCALE_API_URL"); err != nil {
		return c, err
	}
	if c.project, err = getenv("TERRASCALE_PROJECT_ID"); err != nil {
		return c, err
	}
	if c.database, err = getenv("TERRASCALE_DATABASE_ID"); err != nil {
		return c, err
	}
	if c.key, err = getenv("TERRASCALE_API_KEY"); err != nil {
		return c, err
	}
	if c.runID, err = getenv("TERRASCALE_JOURNEY_RUN_ID"); err != nil {
		return c, err
	}
	c.collection = strings.TrimSpace(os.Getenv("TERRASCALE_COLLECTION"))
	c.denyKey = strings.TrimSpace(os.Getenv("TERRASCALE_DENY_API_KEY"))
	if c.denyKey != "" && c.denyKey == c.key {
		return c, errors.New("TERRASCALE_DENY_API_KEY must differ from TERRASCALE_API_KEY")
	}
	if c.collection == "" {
		c.collection = "sdk_journey"
	}
	if !regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$`).MatchString(c.runID) {
		return c, fmt.Errorf("TERRASCALE_JOURNEY_RUN_ID must match [A-Za-z0-9][A-Za-z0-9_-]{0,63}")
	}
	return c, nil
}

func verifySourceInputs(root string) error {
	raw, err := os.ReadFile(filepath.Join(root, "source-input-sha256.txt"))
	if err != nil {
		return fmt.Errorf("read source input manifest: %w", err)
	}
	seen := make(map[string]struct{})
	entries := 0
	for lineNumber, line := range strings.Split(string(raw), "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) != 2 || len(fields[0]) != sha256.Size*2 {
			return fmt.Errorf("invalid source input manifest line %d", lineNumber+1)
		}
		if _, err := hex.DecodeString(fields[0]); err != nil {
			return fmt.Errorf("invalid source input digest on line %d: %w", lineNumber+1, err)
		}
		name := filepath.Clean(fields[1])
		if name != fields[1] || name == "." || filepath.IsAbs(name) || name == ".." || strings.HasPrefix(name, ".."+string(filepath.Separator)) {
			return fmt.Errorf("invalid source input path %q", fields[1])
		}
		if _, ok := seen[name]; ok {
			return fmt.Errorf("duplicate source input %q", name)
		}
		seen[name] = struct{}{}
		entries++
		contents, err := os.ReadFile(filepath.Join(root, name))
		if err != nil {
			return fmt.Errorf("read source input %q: %w", name, err)
		}
		digest := sha256.Sum256(contents)
		if got := hex.EncodeToString(digest[:]); got != fields[0] {
			return fmt.Errorf("source input %q hash mismatch: manifest=%s current=%s", name, fields[0], got)
		}
	}
	if entries == 0 {
		return errors.New("source input manifest is empty")
	}
	return nil
}

func main() {
	if err := verifySourceInputs("."); err != nil {
		fail(err)
	}
	phase := flag.String("phase", "", "seed, verify, exercise, or cleanup")
	flag.Parse()
	if flag.NArg() != 0 || !map[string]bool{"seed": true, "verify": true, "exercise": true, "cleanup": true}[*phase] {
		fail(fmt.Errorf("--phase must be exactly seed, verify, exercise, or cleanup"))
	}
	c, err := loadConfig()
	if err != nil {
		fail(err)
	}
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: c.apiURL, AccessToken: c.key, ProjectID: c.project})
	if err != nil {
		fail(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()
	var runErr error
	switch *phase {
	case "seed":
		runErr = seed(ctx, client, c)
	case "verify":
		runErr = verify(ctx, client, c)
	case "exercise":
		runErr = exercise(ctx, client, c)
	case "cleanup":
		runErr = cleanup(ctx, client, c)
	}
	if runErr != nil {
		fail(runErr)
	}
}

func emit(o observation) {
	o.Detail = redact(o.Detail)
	b, _ := json.Marshal(o)
	fmt.Println(string(b))
}
func redact(detail string) string {
	for _, name := range []string{"TERRASCALE_API_KEY", "TERRASCALE_DENY_API_KEY"} {
		if secret := os.Getenv(name); secret != "" {
			detail = strings.ReplaceAll(detail, secret, "<redacted>")
		}
	}
	if len(detail) > 512 {
		return detail[:512]
	}
	return detail
}
func fail(err error) {
	emit(observation{Phase: "error", Action: "configuration-or-action", Detail: err.Error()})
	os.Exit(1)
}
func id(c config, suffix string) string { return c.runID + "-" + suffix }

func seed(ctx context.Context, c *sdk.Client, cfg config) error {
	_, err := c.Documents.Put(ctx, cfg.database, cfg.collection, id(cfg, "go"), map[string]string{"sdk": "go", "runId": cfg.runID}, sdk.WithIdempotencyKey("go-seed-"+cfg.runID))
	emit(observation{Phase: "seed", Action: "put-own-document", OK: err == nil})
	return err
}

func verify(ctx context.Context, c *sdk.Client, cfg config) error {
	for _, sdkName := range []string{"typescript", "go", "rust", "dotnet"} {
		d, err := c.Documents.Get(ctx, cfg.database, cfg.collection, id(cfg, sdkName))
		if err != nil {
			emit(observation{Phase: "verify", Action: "get-" + sdkName, OK: false, Detail: err.Error()})
			return err
		}
		var body map[string]any
		if err := json.Unmarshal(d.Body, &body); err != nil {
			return fmt.Errorf("decode %s document: %w", sdkName, err)
		}
		if body["sdk"] != sdkName || body["runId"] != cfg.runID {
			return fmt.Errorf("%s document identity mismatch", sdkName)
		}
		emit(observation{Phase: "verify", Action: "get-" + sdkName, OK: true})
	}
	return nil
}

func exercise(ctx context.Context, c *sdk.Client, cfg config) error {
	docID := id(cfg, "go-exercise")
	if _, err := c.Documents.Get(ctx, cfg.database, cfg.collection, id(cfg, "missing")); err == nil {
		return fmt.Errorf("missing document unexpectedly succeeded")
	} else {
		var apiErr *sdk.APIError
		if !errors.As(err, &apiErr) || apiErr.Status != 404 {
			return fmt.Errorf("missing document error = %v, want API status 404", err)
		}
		emit(observation{Phase: "exercise", Action: "missing-document-rejected", OK: true, Detail: "status=404"})
	}
	put, err := c.Documents.Put(ctx, cfg.database, cfg.collection, docID, map[string]any{"sdk": "go", "runId": cfg.runID, "value": 1}, sdk.WithIdempotencyKey("go-exercise-put-"+cfg.runID))
	if err != nil {
		return err
	}
	emit(observation{Phase: "exercise", Action: "put", OK: true})
	replay, err := c.Documents.Put(ctx, cfg.database, cfg.collection, docID, map[string]any{"sdk": "go", "runId": cfg.runID, "value": 1}, sdk.WithIdempotencyKey("go-exercise-put-"+cfg.runID))
	if err != nil || replay.ETag != put.ETag {
		return fmt.Errorf("idempotency replay did not reproduce original write")
	}
	emit(observation{Phase: "exercise", Action: "idempotency-replay", OK: true})
	if _, err = c.Documents.Put(ctx, cfg.database, cfg.collection, docID, map[string]any{"sdk": "go", "runId": cfg.runID, "value": 2}, sdk.WithIfMatch("\"stale-etag\""), sdk.WithIdempotencyKey("go-stale-"+cfg.runID)); err == nil {
		return fmt.Errorf("stale conditional write unexpectedly succeeded")
	} else {
		var apiErr *sdk.APIError
		if !errors.As(err, &apiErr) || apiErr.Status != 412 {
			return fmt.Errorf("stale write error = %v, want API status 412", err)
		}
		emit(observation{Phase: "exercise", Action: "stale-if-match-rejected", OK: true, Detail: "status=412"})
	}
	if put.ETag == "" {
		return fmt.Errorf("put response did not return ETag")
	}
	if _, err = c.Documents.Patch(ctx, cfg.database, cfg.collection, docID, map[string]any{"value": 2}, sdk.WithIfMatch(put.ETag), sdk.WithIdempotencyKey("go-exercise-patch-"+cfg.runID)); err != nil {
		return err
	}
	emit(observation{Phase: "exercise", Action: "patch-current-etag", OK: true})
	result, err := c.ExecuteSQL(ctx, cfg.database, "SELECT id, value FROM "+cfg.collection+" WHERE id = ?", []any{docID})
	if err != nil {
		return err
	}
	if result == nil || len(result.Rows) == 0 || result.Rows[0].ID != docID {
		return fmt.Errorf("parameterized SQL did not return seeded row %s", docID)
	}
	emit(observation{Phase: "exercise", Action: "parameterized-sql", OK: true})
	subCtx, stop := context.WithTimeout(ctx, 15*time.Second)
	defer stop()
	sub, err := c.Subscribe(subCtx, cfg.database, "SELECT * FROM "+cfg.collection+" WHERE id = ?", []any{docID}, sdk.SubscriptionOptions{})
	if err != nil {
		return err
	}
	defer sub.Close()
	if _, err = c.Documents.Patch(ctx, cfg.database, cfg.collection, docID, map[string]any{"value": 3}, sdk.WithIdempotencyKey("go-reactive-"+cfg.runID)); err != nil {
		return err
	}
	for {
		select {
		case update, ok := <-sub.Events():
			if !ok {
				return fmt.Errorf("reactive event channel closed before matching update")
			}
			if update.ID != docID || (update.Kind != sdk.UpdateInsert && update.Kind != sdk.UpdateUpdate && update.Kind != sdk.UpdatePatch) {
				continue
			}
			var body map[string]any
			if err := json.Unmarshal(update.Body, &body); err != nil {
				continue
			}
			if body["value"] != float64(3) {
				continue
			}
			emit(observation{Phase: "exercise", Action: "reactive-update", OK: true})
			goto reactiveDone
		case <-subCtx.Done():
			return fmt.Errorf("reactive update timeout: %w", subCtx.Err())
		}
	}
reactiveDone:
	if cfg.denyKey != "" {
		denied, err := sdk.NewClient(sdk.Config{APIBaseURL: cfg.apiURL, AccessToken: cfg.denyKey, ProjectID: cfg.project})
		if err != nil {
			return err
		}
		if _, err = denied.Documents.Get(ctx, cfg.database, cfg.collection, docID); err == nil {
			return fmt.Errorf("denied API key unexpectedly read exercise document")
		} else {
			var apiErr *sdk.APIError
			if !errors.As(err, &apiErr) || (apiErr.Status != 401 && apiErr.Status != 403) {
				return fmt.Errorf("denied API key error = %v, want API status 401 or 403", err)
			}
			emit(observation{Phase: "exercise", Action: "denied-key-rejected", OK: true, Detail: fmt.Sprintf("status=%d", apiErr.Status)})
		}
	}
	return nil
}

func cleanup(ctx context.Context, c *sdk.Client, cfg config) error {
	var cleanupErr error
	for _, suffix := range []string{"go", "go-exercise"} {
		if err := c.Documents.Delete(ctx, cfg.database, cfg.collection, id(cfg, suffix), sdk.WithIdempotencyKey("go-cleanup-"+suffix+"-"+cfg.runID)); err != nil {
			var apiErr *sdk.APIError
			if !errors.As(err, &apiErr) || apiErr.Status != 404 {
				cleanupErr = errors.Join(cleanupErr, err)
				emit(observation{Phase: "cleanup", Action: "delete-" + suffix, OK: false, Detail: err.Error()})
				continue
			}
		}
		emit(observation{Phase: "cleanup", Action: "delete-" + suffix, OK: true})
	}
	return cleanupErr
}
