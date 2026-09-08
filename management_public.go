package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
)

// IndexRecommendations carries the coordinator generation and bounded candidates.
type IndexRecommendations struct {
	Generation      uint64                `json:"generation"`
	Truncated       bool                  `json:"truncated"`
	Recommendations []IndexRecommendation `json:"recommendations"`
}
type IndexRecommendation struct {
	CandidateID     string   `json:"candidate_id"`
	Collection      string   `json:"collection"`
	Fields          []string `json:"fields"`
	Score           float64  `json:"score"`
	MaintenanceCost uint64   `json:"maintenance_cost"`
	Generation      uint64   `json:"generation"`
	NeedsDescriptor bool     `json:"needs_descriptor"`
}

// SchemaDocument preserves identity, concurrency generation and migration facts.
type SchemaDocument struct {
	Target        SchemaTarget           `json:"target"`
	Identity      SchemaDatabaseIdentity `json:"identity"`
	Schema        CollectionSchema       `json:"schema"`
	Generation    uint64                 `json:"generation"`
	SchemaVersion uint64                 `json:"schema_version"`
	Migration     *SchemaMigration       `json:"migration,omitempty"`
}
type SchemaTarget struct {
	Database   string `json:"database"`
	Branch     string `json:"branch"`
	Collection string `json:"collection"`
}
type SchemaDatabaseIdentity struct {
	DatabaseID string       `json:"database_id"`
	Placement  []RegionCode `json:"placement"`
}
type SchemaMigration struct {
	OperationID string                `json:"operation_id"`
	State       string                `json:"state"`
	Steps       []SchemaMigrationStep `json:"steps"`
	Total       uint64                `json:"total"`
	Completed   uint64                `json:"completed"`
	NextCursor  *string               `json:"next_cursor,omitempty"`
	Message     string                `json:"message"`
}
type SchemaMigrationStep struct {
	Kind  string  `json:"kind"`
	From  *string `json:"from,omitempty"`
	To    *string `json:"to,omitempty"`
	Index *string `json:"index,omitempty"`
}

// ExportAccepted identifies an operation that continues after disconnection.
type ExportAccepted struct {
	JobID        string `json:"job_id"`
	StatusPath   string `json:"status_path"`
	ResumeCursor string `json:"resume_cursor"`
}
type ExportState string

const (
	ExportAcceptedState ExportState = "Accepted"
	ExportRunning       ExportState = "Running"
	ExportComplete      ExportState = "Complete"
	ExportFailed        ExportState = "Failed"
	ExportCancelled     ExportState = "Cancelled"
)

type ExportPhase string

const (
	ExportDocuments ExportPhase = "Documents"
	ExportHistory   ExportPhase = "History"
	ExportDone      ExportPhase = "Done"
)

type ExportCancellation string

const (
	ExportCancellationAvailable   ExportCancellation = "available"
	ExportCancellationRequested   ExportCancellation = "requested"
	ExportCancellationComplete    ExportCancellation = "complete"
	ExportCancellationUnavailable ExportCancellation = "unavailable"
)

type ExportStatus struct {
	CreatedAtMillis     uint64             `json:"created_at_millis"`
	Phase               ExportPhase        `json:"phase"`
	Cancellation        ExportCancellation `json:"cancellation"`
	Pages               uint64             `json:"pages"`
	JobID               string             `json:"job_id"`
	Status              ExportState        `json:"status"`
	Records             uint64             `json:"records"`
	EstimatedRecords    *uint64            `json:"estimated_records"`
	ProgressBasisPoints uint16             `json:"progress_basis_points"`
	ResumeCursor        string             `json:"resume_cursor"`
	Manifest            *ExportArtifact    `json:"manifest"`
	Error               *string            `json:"error"`
}
type ExportArtifact struct {
	Path     string `json:"path"`
	Length   uint64 `json:"length"`
	Checksum string `json:"checksum"`
}

// CreateExport admits one export using a stable producer-side retry identity.
func (d *DatabasesClient) CreateExport(ctx context.Context, database, idempotencyKey string, opts ...RequestOption) (*ExportAccepted, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
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
		Key string `json:"idempotency_key"`
	}{key}
	var out ExportAccepted
	if err := d.c.do(ctx, "POST", path+"/exports", nil, body, &out, "create_export", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
func (d *DatabasesClient) GetExport(ctx context.Context, database, jobID string, opts ...RequestOption) (*ExportStatus, error) {
	path, err := managementResourcePath(ctx, database, "exports", jobID)
	if err != nil {
		return nil, err
	}
	var out ExportStatus
	if err := d.c.do(ctx, "GET", path, nil, nil, &out, "get_export", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// ResumeExport reuses the job and latest opaque cursor; it does not create a job.
func (d *DatabasesClient) ResumeExport(ctx context.Context, database, jobID, cursor string, opts ...RequestOption) (*ExportStatus, error) {
	path, err := managementResourcePath(ctx, database, "exports", jobID)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(cursor) == "" {
		return nil, &ValidationError{Field: "resume_cursor", Message: "must be nonblank"}
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	body := struct {
		Cursor string `json:"resume_cursor"`
	}{cursor}
	var out ExportStatus
	if err := d.c.do(ctx, "POST", path+"/resume", nil, body, &out, "resume_export", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// WebhookEndpoint never contains the producer's signing secret.
type WebhookEndpoint struct {
	ID        string `json:"id"`
	Endpoint  string `json:"endpoint"`
	CreatedAt uint64 `json:"created_at"`
	Deleted   bool   `json:"deleted"`
}

// WebhookSecretResponse is returned only by create/rotation. Decode Secret
// as unpadded base64url before verifying signatures; store it securely.
type WebhookSecretResponse struct {
	WebhookEndpoint
	Secret string `json:"secret"`
}
type RotateWebhookSecretRequest struct {
	OverlapSeconds *uint32 `json:"overlap_seconds,omitempty"`
}

type WebhookList struct {
	Webhooks []WebhookEndpoint `json:"webhooks"`
}
type WebhookDelivery struct {
	ID             string  `json:"id"`
	EventID        string  `json:"event_id"`
	Revision       uint64  `json:"revision"`
	RetryStartedAt uint64  `json:"retry_started_at"`
	Attempts       uint32  `json:"attempts"`
	NextAttemptAt  uint64  `json:"next_attempt_at"`
	Status         *uint16 `json:"status"`
	Delivered      bool    `json:"delivered"`
	Terminal       bool    `json:"terminal"`
}
type WebhookDeliveries struct {
	Deliveries []WebhookDelivery `json:"deliveries"`
}

func (d *DatabasesClient) ListWebhooks(ctx context.Context, database string, opts ...RequestOption) (*WebhookList, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	var out WebhookList
	if err := d.c.do(ctx, "GET", path+"/webhooks", nil, nil, &out, "list_webhooks", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
func (d *DatabasesClient) CreateWebhook(ctx context.Context, database, endpoint string, opts ...RequestOption) (*WebhookSecretResponse, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	parsed, err := url.Parse(endpoint)
	if err != nil || parsed.Scheme != "https" || parsed.Hostname() == "" || parsed.User != nil || parsed.Fragment != "" {
		return nil, &ValidationError{Field: "endpoint", Message: "must be an absolute HTTPS URL without userinfo or fragment"}
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	body := struct {
		Endpoint string `json:"endpoint"`
	}{endpoint}
	var out WebhookSecretResponse
	if err := d.c.do(ctx, "POST", path+"/webhooks", nil, body, &out, "create_webhook", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
func (d *DatabasesClient) DeleteWebhook(ctx context.Context, database, id string, opts ...RequestOption) (*WebhookEndpoint, error) {
	path, err := managementResourcePath(ctx, database, "webhooks", id)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out WebhookEndpoint
	if err := d.c.do(ctx, "DELETE", path, nil, nil, &out, "delete_webhook", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
func (d *DatabasesClient) ListWebhookDeliveries(ctx context.Context, database, id string, opts ...RequestOption) (*WebhookDeliveries, error) {
	path, err := managementResourcePath(ctx, database, "webhooks", id)
	if err != nil {
		return nil, err
	}
	var out WebhookDeliveries
	if err := d.c.do(ctx, "GET", path+"/deliveries", nil, nil, &out, "list_deliveries", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
func (d *DatabasesClient) ReplayWebhookDelivery(ctx context.Context, database, id, deliveryID string, opts ...RequestOption) (*WebhookDelivery, error) {
	path, err := managementResourcePath(ctx, database, "webhooks", id)
	if err != nil {
		return nil, err
	}
	if err := requireID("delivery_id", deliveryID); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out WebhookDelivery
	if err := d.c.do(ctx, "POST", path+"/deliveries/"+url.PathEscape(deliveryID)+"/replay", nil, nil, &out, "replay_webhook", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

func managementResourcePath(ctx context.Context, database, kind, id string) (string, error) {
	if err := managementContext(ctx); err != nil {
		return "", err
	}
	path, err := databasePath(database)
	if err != nil {
		return "", err
	}
	if err := requireID(kind+" id", id); err != nil {
		return "", err
	}
	return path + "/" + kind + "/" + url.PathEscape(id), nil
}

// ExportDownload contains one decrypted artifact member and its plaintext
// integrity metadata returned by the server.
type ExportDownload struct {
	Bytes    []byte
	Length   uint64
	Checksum string
}

// DownloadExport retrieves one decrypted artifact member after export completion.
// The member is escaped as one path segment; JSON is never applied to its bytes.
func (d *DatabasesClient) DownloadExport(ctx context.Context, database, jobID, member string, opts ...RequestOption) (*ExportDownload, error) {
	path, err := managementResourcePath(ctx, database, "exports", jobID)
	if err != nil {
		return nil, err
	}
	if err := requireID("member", member); err != nil {
		return nil, err
	}
	opts = append(opts, func(rc *requestConfig) { rc.responseMediaType = "application/octet-stream" })
	var headers http.Header
	opts = append(opts, func(rc *requestConfig) { rc.responseHeaders = &headers })
	var out binaryResponse
	if err := d.c.do(ctx, "GET", path+"/artifacts/"+url.PathEscape(member), nil, nil, &out, "download_export", opts...); err != nil {
		return nil, err
	}
	lengthValues := headers.Values("X-TerraScale-Artifact-Length")
	if len(lengthValues) != 1 {
		return nil, fmt.Errorf("sdk: invalid artifact length header")
	}
	length, err := strconv.ParseUint(lengthValues[0], 10, 64)
	if err != nil || strconv.FormatUint(length, 10) != lengthValues[0] {
		return nil, fmt.Errorf("sdk: invalid artifact length header")
	}
	checksumValues := headers.Values("X-TerraScale-Artifact-Checksum")
	if len(checksumValues) != 1 {
		return nil, fmt.Errorf("sdk: invalid artifact checksum header")
	}
	checksum := checksumValues[0]
	if len(checksum) != 64 || strings.Trim(checksum, "0123456789abcdefABCDEF") != "" {
		return nil, fmt.Errorf("sdk: invalid artifact checksum header")
	}
	if uint64(len(out)) != length {
		return nil, fmt.Errorf("sdk: artifact length header does not match body")
	}
	return &ExportDownload{Bytes: []byte(out), Length: length, Checksum: strings.ToLower(checksum)}, nil
}

// ProvisioningOperation acknowledges durable work; it does not imply readiness.
type ProvisioningOperation struct {
	Environment      string        `json:"environment"`
	TenantID         string        `json:"tenant_id"`
	ProjectID        string        `json:"project_id"`
	OperationID      string        `json:"operation_id"`
	DatabaseID       string        `json:"database_id"`
	ParentDatabaseID *string       `json:"parent_database_id"`
	KeyVersion       string        `json:"key_version"`
	Name             string        `json:"name"`
	Regions          []RegionCode  `json:"regions"`
	Residency        PlacementLock `json:"residency"`
	State            string        `json:"state"`
}

// CreateDatabaseResult distinguishes immediate creation from accepted work.
// Exactly one of Database (201) and Operation (202) is populated.
type CreateDatabaseResult struct {
	Status    int                    `json:"status"`
	Database  *DatabaseRecord        `json:"database,omitempty"`
	Operation *ProvisioningOperation `json:"operation,omitempty"`
}

func decodeProvisioningOperation(status int, body []byte) (*ProvisioningOperation, error) {
	if status != http.StatusAccepted {
		return nil, fmt.Errorf("sdk: expected accepted provisioning response, got status %d", status)
	}
	var operation ProvisioningOperation
	if err := json.Unmarshal(body, &operation); err != nil {
		return nil, err
	}
	if operation.Environment == "" || operation.TenantID == "" || operation.ProjectID == "" || operation.OperationID == "" || operation.DatabaseID == "" || operation.KeyVersion == "" || operation.Name == "" || len(operation.Regions) == 0 || (operation.State != "accepted" && operation.State != "pending") {
		return nil, fmt.Errorf("sdk: accepted operation has no valid provisioning identity")
	}
	return &operation, nil
}

type acceptedProvisioningOperation struct {
	ProvisioningOperation
}

func (o *acceptedProvisioningOperation) decodeHTTPResponse(status int, body []byte) error {
	operation, err := decodeProvisioningOperation(status, body)
	if err != nil {
		return err
	}
	o.ProvisioningOperation = *operation
	return nil
}

func (r *CreateDatabaseResult) decodeHTTPResponse(status int, body []byte) error {
	*r = CreateDatabaseResult{Status: status}
	switch status {
	case 201:
		var database DatabaseRecord
		if err := json.Unmarshal(body, &database); err != nil {
			return err
		}
		if database.Name == "" {
			return fmt.Errorf("sdk: create response has no database name")
		}
		r.Database = &database
	case 202:
		var operation ProvisioningOperation
		if err := json.Unmarshal(body, &operation); err != nil {
			return err
		}
		if operation.Environment == "" || operation.TenantID == "" || operation.ProjectID == "" || operation.OperationID == "" || operation.DatabaseID == "" || operation.KeyVersion == "" || operation.Name == "" || len(operation.Regions) == 0 || (operation.State != "accepted" && operation.State != "pending") {
			return fmt.Errorf("sdk: accepted create response has no valid provisioning operation")
		}
		r.Operation = &operation
	default:
		return fmt.Errorf("sdk: unexpected database create success status %d", status)
	}
	return nil
}

// RotateWebhookSecret returns a new secret once, retaining the previous
// signature for the requested overlap (default 300 seconds, maximum 3600).
func (d *DatabasesClient) RotateWebhookSecret(ctx context.Context, database, id string, request RotateWebhookSecretRequest, opts ...RequestOption) (*WebhookSecretResponse, error) {
	path, err := managementResourcePath(ctx, database, "webhooks", id)
	if err != nil {
		return nil, err
	}
	if request.OverlapSeconds != nil && *request.OverlapSeconds > 3600 {
		return nil, &ValidationError{Field: "overlap_seconds", Message: "must not exceed 3600"}
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	// Rotation has no idempotency replay contract, even if shared options carry a key.
	opts = append(opts, WithoutIdempotency())
	var out WebhookSecretResponse
	if err := d.c.do(ctx, "POST", path+"/rotate-secret", nil, request, &out, "rotate_secret", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// DevelopmentFaultKind selects a bounded fault on an authorized cloud branch.
type DevelopmentFaultKind string

const (
	DevelopmentUnavailable DevelopmentFaultKind = "unavailable"
	DevelopmentLatency     DevelopmentFaultKind = "latency"
)

type DevelopmentFaultRequest struct {
	ConfirmBranch string               `json:"confirmBranch"`
	Fault         DevelopmentFaultKind `json:"fault"`
	DurationMS    uint64               `json:"durationMs"`
	MaxOperations uint16               `json:"maxOperations"`
	DelayMS       uint64               `json:"delayMs"`
}
type DevelopmentFaultResponse struct {
	Active          bool   `json:"active"`
	ExpiresAtMillis uint64 `json:"expiresAtMillis"`
}
type DevelopmentLogEntry struct {
	Sequence uint64 `json:"sequence"`
	Action   string `json:"action"`
	AtMillis uint64 `json:"atMillis"`
}
type DevelopmentLogResponse struct {
	Entries   []DevelopmentLogEntry `json:"entries"`
	Truncated bool                  `json:"truncated"`
}

// EnableDevelopmentFault requires branch confirmation and server-side database
// administration authority. Production database eligibility is enforced by API.
func (d *DatabasesClient) EnableDevelopmentFault(ctx context.Context, database string, request DevelopmentFaultRequest, opts ...RequestOption) (*DevelopmentFaultResponse, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	validDelay := request.Fault == DevelopmentUnavailable && request.DelayMS == 0 || request.Fault == DevelopmentLatency && request.DelayMS >= 1 && request.DelayMS <= 1000
	if request.ConfirmBranch != database || request.DurationMS < 1 || request.DurationMS > 30000 || request.MaxOperations < 1 || request.MaxOperations > 100 || !validDelay {
		return nil, &ValidationError{Field: "development fault", Message: "confirm the branch; durationMs must be 1..30000, maxOperations 1..100, latency delayMs 1..1000 and unavailable delayMs 0"}
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out DevelopmentFaultResponse
	if err := d.c.do(ctx, "POST", path+"/dev/faults", nil, request, &out, "development_fault", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
func (d *DatabasesClient) ClearDevelopmentFault(ctx context.Context, database string, opts ...RequestOption) (*DevelopmentFaultResponse, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out DevelopmentFaultResponse
	if err := d.c.do(ctx, "DELETE", path+"/dev/faults", nil, nil, &out, "clear_development_fault", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// DevelopmentLogs reads bounded, redacted branch index lifecycle diagnostics.
func (d *DatabasesClient) DevelopmentLogs(ctx context.Context, database string, limit int, opts ...RequestOption) (*DevelopmentLogResponse, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if limit < 0 || limit > 100 {
		return nil, &ValidationError{Field: "limit", Message: "must be between 1 and 100, or zero for the server default"}
	}
	query := url.Values{}
	if limit > 0 {
		query.Set("limit", fmt.Sprint(limit))
	}
	var out DevelopmentLogResponse
	if err := d.c.do(ctx, "GET", path+"/dev/logs", query, nil, &out, "development_logs", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// OperationsClient reads authoritative operation state without repeating work.
type OperationsClient struct{ c *Client }

// Get discovers durable operation progress by the original operation identity.
// The producer returns the canonical OperationSummary projection for both
// control-plane and member-owned operation journals.
func (o *OperationsClient) Get(ctx context.Context, operationID string, opts ...RequestOption) (*OperationSummary, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("operation_id", operationID); err != nil {
		return nil, err
	}
	var out OperationSummary
	if err := o.c.do(ctx, "GET", "/api/v1/operations/"+url.PathEscape(operationID), nil, nil, &out, "get_provisioning_operation", opts...); err != nil {
		return nil, err
	}
	if out.OperationID != operationID {
		return nil, fmt.Errorf("sdk: operation response identity does not match the request")
	}
	return &out, nil
}

// ExportList exposes durable jobs and an opaque cursor for the next page.
type ExportList struct {
	Exports    []ExportStatus `json:"exports"`
	NextCursor *string        `json:"next_cursor"`
}

func (d *DatabasesClient) ListExports(ctx context.Context, database, after string, limit int, opts ...RequestOption) (*ExportList, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if limit < 0 || limit > 50 {
		return nil, &ValidationError{Field: "limit", Message: "must be between 1 and 50, or zero for the server default"}
	}
	query := url.Values{}
	if after != "" {
		query.Set("after", after)
	}
	if limit > 0 {
		query.Set("limit", fmt.Sprint(limit))
	}
	var out ExportList
	if err := d.c.do(ctx, "GET", path+"/exports", query, nil, &out, "list_exports", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CancelExport requests cancellation at a safe boundary. Inspect the returned
// cancellation state; an accepted request does not mean work has stopped yet.
func (d *DatabasesClient) CancelExport(ctx context.Context, database, jobID string, opts ...RequestOption) (*ExportStatus, error) {
	path, err := managementResourcePath(ctx, database, "exports", jobID)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out ExportStatus
	if err := d.c.do(ctx, "POST", path+"/cancel", nil, nil, &out, "cancel_export", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
