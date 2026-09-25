// The browser integration test sends this helper account-scoped credentials on
// stdin so the Go SDK can exercise the public API without storing secrets.
package main

import (
	"context"
	json "encoding/json/v2"
	"errors"
	"fmt"
	"os"
	"time"

	sdk "terrascale.tech/sdk"
)

type input struct {
	APIBaseURL      string `json:"apiBaseUrl"`
	Account         string `json:"account"`
	TenantID        string `json:"tenantId"`
	ProjectID       string `json:"projectId"`
	PrimaryDatabase string `json:"primaryDatabase"`
	OtherDatabase   string `json:"otherDatabase"`
	DocumentID      string `json:"documentId"`
	WriteKey        string `json:"writeKey"`
	ReadOnlyKey     string `json:"readOnlyKey"`
}

type documentValue struct {
	Account    string `json:"account"`
	Source     string `json:"source"`
	DocumentID string `json:"documentId"`
}

func client(cfg input, key string) (*sdk.Client, error) {
	return sdk.NewClient(sdk.Config{
		APIBaseURL:  cfg.APIBaseURL,
		AccessToken: key,
		TenantID:    cfg.TenantID,
		ProjectID:   cfg.ProjectID,
	})
}

func expectStatus(err error, status int) error {
	if apiErr, ok := errors.AsType[*sdk.APIError](err); ok && apiErr.Status == status {
		return nil
	}
	return fmt.Errorf("expected HTTP %d, got %v", status, err)
}

func run(ctx context.Context) error {
	var cfg input
	if err := json.UnmarshalRead(os.Stdin, &cfg); err != nil {
		return fmt.Errorf("decode client input: %w", err)
	}
	writer, err := client(cfg, cfg.WriteKey)
	if err != nil {
		return fmt.Errorf("create writer: %w", err)
	}
	reader, err := client(cfg, cfg.ReadOnlyKey)
	if err != nil {
		return fmt.Errorf("create reader: %w", err)
	}
	invalid, err := client(cfg, "invalid-sdk-fixture-key")
	if err != nil {
		return fmt.Errorf("create invalid client: %w", err)
	}

	value := documentValue{Account: cfg.Account, Source: "ts-go", DocumentID: cfg.DocumentID}
	written, err := writer.Documents.Put(ctx, cfg.PrimaryDatabase, "accounts", cfg.DocumentID, value, sdk.WithIdempotencyKey(cfg.DocumentID))
	if err != nil {
		return fmt.Errorf("write document: %w", err)
	}
	if written.Revision == "" {
		return errors.New("write document returned no revision")
	}
	readAndCompare := func(name string, sdkClient *sdk.Client) error {
		document, err := sdkClient.Documents.Get(ctx, cfg.PrimaryDatabase, "accounts", cfg.DocumentID)
		if err != nil {
			return fmt.Errorf("%s read document: %w", name, err)
		}
		var actual documentValue
		if err := json.Unmarshal(document.Body, &actual); err != nil {
			return fmt.Errorf("%s decode document: %w", name, err)
		}
		if actual != value {
			return fmt.Errorf("%s read different document: got %+v, want %+v", name, actual, value)
		}
		return nil
	}
	if err := readAndCompare("writer", writer); err != nil {
		return err
	}
	if err := expectStatusFromGet(ctx, invalid, cfg.PrimaryDatabase, cfg.DocumentID, 401); err != nil {
		return fmt.Errorf("invalid credential: %w", err)
	}
	if err := readAndCompare("read-only", reader); err != nil {
		return err
	}
	_, err = reader.Documents.Put(ctx, cfg.PrimaryDatabase, "accounts", cfg.DocumentID+"-blocked", map[string]bool{"blocked": true})
	if err := expectStatus(err, 403); err != nil {
		return fmt.Errorf("read-only write: %w", err)
	}
	if err := expectStatusFromGet(ctx, writer, cfg.OtherDatabase, cfg.DocumentID, 403); err != nil {
		return fmt.Errorf("cross-database read: %w", err)
	}
	if err := writer.Documents.Delete(ctx, cfg.PrimaryDatabase, "accounts", cfg.DocumentID, sdk.WithIdempotencyKey(cfg.DocumentID+"-delete")); err != nil {
		return fmt.Errorf("delete document: %w", err)
	}
	if err := expectStatusFromGet(ctx, writer, cfg.PrimaryDatabase, cfg.DocumentID, 404); err != nil {
		return fmt.Errorf("read after delete: %w", err)
	}
	return nil
}

func expectStatusFromGet(ctx context.Context, client *sdk.Client, database, documentID string, status int) error {
	_, err := client.Documents.Get(ctx, database, "accounts", documentID)
	return expectStatus(err, status)
}

func main() {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := run(ctx); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
