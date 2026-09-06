package sdk

import (
	"context"
	"net/url"
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
)

type ExportStatus struct {
	JobID               string          `json:"job_id"`
	Status              ExportState     `json:"status"`
	Records             uint64          `json:"records"`
	EstimatedRecords    *uint64         `json:"estimated_records"`
	ProgressBasisPoints uint16          `json:"progress_basis_points"`
	ResumeCursor        string          `json:"resume_cursor"`
	Manifest            *ExportArtifact `json:"manifest"`
	Error               *string         `json:"error"`
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
func (d *DatabasesClient) CreateWebhook(ctx context.Context, database, endpoint string, opts ...RequestOption) (*WebhookEndpoint, error) {
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
	var out WebhookEndpoint
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
