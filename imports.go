package sdk

import (
	"bytes"
	"context"
	"net/http"
	"strings"
)

// TransferFormat identifies a supported import or export interchange format.
type TransferFormat string

const (
	TransferFormatNative     TransferFormat = "native"
	TransferFormatJSONL      TransferFormat = "jsonl"
	TransferFormatCSV        TransferFormat = "csv"
	TransferFormatMongoJSON  TransferFormat = "mongo_json"
	TransferFormatCosmosJSON TransferFormat = "cosmos_json"
	TransferFormatDynamoJSON TransferFormat = "dynamo_json"
)

// ImportJob reports durable import admission, upload, and execution progress.
type ImportJob struct {
	OperationID      string  `json:"operation_id"`
	TenantID         string  `json:"tenant_id"`
	ProjectID        string  `json:"project_id"`
	DatabaseID       string  `json:"database_id"`
	Format           string  `json:"format"`
	Durability       string  `json:"durability"`
	PendingOperation *string `json:"pending_operation"`
	PendingCursor    *uint64 `json:"pending_cursor"`
	PendingRecords   *uint64 `json:"pending_records"`
	InputDigest      *string `json:"input_digest"`
	InputLength      *uint64 `json:"input_length"`
	InputCommitted   bool    `json:"input_committed"`
	Cursor           uint64  `json:"cursor"`
	CommittedRecords uint64  `json:"committed_records"`
	State            string  `json:"state"`
	Error            *string `json:"error"`
}

type importPayload []byte

func validateImportFormat(format TransferFormat) error {
	if format == TransferFormatNative {
		return &ValidationError{Field: "format", Message: "native bundles require native restore; choose a document interchange format"}
	}
	switch format {
	case TransferFormatJSONL, TransferFormatCSV, TransferFormatMongoJSON, TransferFormatCosmosJSON, TransferFormatDynamoJSON:
		return nil
	default:
		return &ValidationError{Field: "format", Message: "must be jsonl, csv, mongo_json, cosmos_json, or dynamo_json"}
	}
}

// CreateImport admits an import and returns its durable job identity.
func (d *DatabasesClient) CreateImport(ctx context.Context, database string, format TransferFormat, idempotencyKey string, opts ...RequestOption) (*ImportJob, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if err := validateImportFormat(format); err != nil {
		return nil, err
	}
	key := strings.TrimSpace(idempotencyKey)
	if key == "" {
		return nil, &IdempotencyError{Reason: "must be nonblank"}
	}
	opts = append(opts, WithIdempotencyKey(key))
	if err := requiredManagementMutation(opts); err != nil {
		return nil, err
	}
	body := struct {
		Format         TransferFormat `json:"format"`
		IdempotencyKey string         `json:"idempotency_key"`
	}{format, key}
	var out ImportJob
	if err := d.c.do(ctx, http.MethodPost, path+"/imports", nil, body, &out, "create_import", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetImport returns durable progress for one admitted import.
func (d *DatabasesClient) GetImport(ctx context.Context, database, id string, opts ...RequestOption) (*ImportJob, error) {
	path, err := managementResourcePath(ctx, database, "imports", id)
	if err != nil {
		return nil, err
	}
	var out ImportJob
	if err := d.c.do(ctx, http.MethodGet, path, nil, nil, &out, "get_import", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListImports returns all imports admitted for the database.
func (d *DatabasesClient) ListImports(ctx context.Context, database string, opts ...RequestOption) ([]ImportJob, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	var out []ImportJob
	if err := d.c.do(ctx, http.MethodGet, path+"/imports", nil, nil, &out, "list_imports", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// UploadImport commits the opaque import input and queues execution.
func (d *DatabasesClient) UploadImport(ctx context.Context, database, id string, input []byte, opts ...RequestOption) (*ImportJob, error) {
	path, err := managementResourcePath(ctx, database, "imports", id)
	if err != nil {
		return nil, err
	}
	var out ImportJob
	if err := d.c.do(ctx, http.MethodPut, path+"/input", nil, importPayload(bytes.Clone(input)), &out, "upload_import", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// ResumeImport asks the server to resolve uncertain batches and continue work.
func (d *DatabasesClient) ResumeImport(ctx context.Context, database, id string, opts ...RequestOption) (*ImportJob, error) {
	path, err := managementResourcePath(ctx, database, "imports", id)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out ImportJob
	if err := d.c.do(ctx, http.MethodPost, path+"/resume", nil, nil, &out, "resume_import", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
