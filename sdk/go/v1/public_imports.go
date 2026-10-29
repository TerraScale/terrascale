package v1

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"strconv"
	"time"
)

// ImportOptions selects missing-primary-key handling and excluded source objects.
type ImportOptions struct {
	MissingPrimaryKey string   `json:"missingPrimaryKey,omitempty"`
	Exclude           []string `json:"exclude,omitempty"`
}

// CreateImportRequest attaches postgres_plain or sqlite source to an empty database.
type CreateImportRequest struct {
	Format       string         `json:"format"`
	SourceBytes  int64          `json:"sourceBytes"`
	SourceSHA256 string         `json:"sourceSha256"`
	Options      *ImportOptions `json:"options,omitzero"`
}

// ImportSource describes the staged source and its retention deadline.
type ImportSource struct {
	Format        string     `json:"format"`
	Bytes         int64      `json:"bytes"`
	SHA256        string     `json:"sha256"`
	ReceivedBytes int64      `json:"receivedBytes"`
	Staged        bool       `json:"staged"`
	RetainedUntil *time.Time `json:"retainedUntil"`
}

// ImportColumn describes how a source column maps into a document.
type ImportColumn struct {
	Name       string `json:"name"`
	SourceType string `json:"sourceType"`
	Mapping    string `json:"mapping"`
	Nullable   bool   `json:"nullable"`
	Excluded   bool   `json:"excluded"`
}

// ImportConstraint records a source constraint; imports do not enforce it.
type ImportConstraint struct {
	Kind       string   `json:"kind"`
	Name       *string  `json:"name"`
	Columns    []string `json:"columns"`
	References *string  `json:"references"`
	Enforced   bool     `json:"enforced"`
}

// ImportIssue is a blocking or warning preview diagnostic.
type ImportIssue struct {
	Severity string  `json:"severity"`
	Code     string  `json:"code"`
	Column   *string `json:"column"`
	Row      *int64  `json:"row"`
	Message  string  `json:"message"`
}

// ImportTable describes one table's planned destination and ID policy.
type ImportTable struct {
	Source      string             `json:"source"`
	Collection  string             `json:"collection"`
	Excluded    bool               `json:"excluded"`
	Rows        int64              `json:"rows"`
	IDPolicy    string             `json:"idPolicy"`
	PrimaryKey  []string           `json:"primaryKey"`
	Columns     []ImportColumn     `json:"columns"`
	Constraints []ImportConstraint `json:"constraints"`
	Issues      []ImportIssue      `json:"issues"`
}

// ImportSkipped identifies a source object omitted from the import.
type ImportSkipped struct {
	Kind string `json:"kind"`
	Name string `json:"name"`
}

// ImportPreview is the dry-run result whose ULID must be confirmed.
type ImportPreview struct {
	ID               string          `json:"id" ulid:"required"`
	Tables           []ImportTable   `json:"tables"`
	Skipped          []ImportSkipped `json:"skipped"`
	SkippedTruncated bool            `json:"skippedTruncated"`
	Blocking         int             `json:"blocking"`
	Warnings         int             `json:"warnings"`
}

// ImportProgress reports copy progress without client-side polling.
type ImportProgress struct {
	TablesDone   int     `json:"tablesDone"`
	TablesTotal  int     `json:"tablesTotal"`
	RowsWritten  int64   `json:"rowsWritten"`
	RowsTotal    int64   `json:"rowsTotal"`
	CurrentTable *string `json:"currentTable"`
}

// ImportTableValidation compares source and read-back document checksums.
type ImportTableValidation struct {
	Source           string `json:"source"`
	Collection       string `json:"collection"`
	SourceRows       int64  `json:"sourceRows"`
	Documents        int64  `json:"documents"`
	SourceChecksum   string `json:"sourceChecksum"`
	DocumentChecksum string `json:"documentChecksum"`
	Match            bool   `json:"match"`
}

// ImportValidation reports validation of all imported tables.
type ImportValidation struct {
	Passed bool                    `json:"passed"`
	Tables []ImportTableValidation `json:"tables"`
}

// ImportError describes a job failure separately from HTTP request errors.
type ImportError struct {
	Code      string  `json:"code"`
	Message   string  `json:"message"`
	Retryable bool    `json:"retryable"`
	Table     *string `json:"table"`
	Row       *int64  `json:"row"`
	Column    *string `json:"column"`
}

// ImportJob is the producer's complete import state, preview, and diagnostics.
type ImportJob struct {
	ID                 string            `json:"id" ulid:"required"`
	Project            string            `json:"project" ulid:"required"`
	Database           string            `json:"database"`
	Region             string            `json:"region"`
	State              string            `json:"state"`
	CancelRequested    bool              `json:"cancelRequested"`
	Options            ImportOptions     `json:"options"`
	Source             ImportSource      `json:"source"`
	Preview            *ImportPreview    `json:"preview"`
	ConfirmedPreviewID *string           `json:"confirmedPreviewId" ulid:"optional"`
	Progress           *ImportProgress   `json:"progress"`
	Validation         *ImportValidation `json:"validation"`
	Error              *ImportError      `json:"error"`
	CreatedAt          time.Time         `json:"createdAt"`
	UpdatedAt          time.Time         `json:"updatedAt"`
	StartedAt          *time.Time        `json:"startedAt"`
	FinishedAt         *time.Time        `json:"finishedAt"`
}

// CreateImport attaches a source declaration to a new empty database.
func (d *Database) CreateImport(ctx context.Context, in CreateImportRequest) (*ImportJob, error) {
	return databaseCall[ImportJob](ctx, d, http.MethodPost, nil, in, "import")
}

// Import reads the current import job. It does not poll automatically.
func (d *Database) Import(ctx context.Context) (*ImportJob, error) {
	return databaseCall[ImportJob](ctx, d, http.MethodGet, nil, nil, "import")
}

// UpdateImport changes options and asks the producer to analyze again.
func (d *Database) UpdateImport(ctx context.Context, options ImportOptions) (*ImportJob, error) {
	return databaseCall[ImportJob](ctx, d, http.MethodPatch, nil, struct {
		Options ImportOptions `json:"options"`
	}{options}, "import")
}

// UploadImportSource stages one ordered 1-byte to 4-MiB source part.
// Offset is source.receivedBytes; an uncertain upload must be reconciled by reading the job.
func (d *Database) UploadImportSource(ctx context.Context, offset int64, part []byte) (*ImportJob, error) {
	if offset < 0 || len(part) == 0 || len(part) > 4<<20 {
		return nil, errors.New("terrabase: import part requires nonnegative offset and 1 to 4194304 bytes")
	}
	p, err := d.path("import", "source")
	if err != nil {
		return nil, err
	}
	return publicCall[ImportJob](ctx, d.c, request{method: http.MethodPut, path: p, query: url.Values{"offset": {strconv.FormatInt(offset, 10)}}, body: part, contentType: "application/octet-stream"}, nil)
}

// StartImport confirms the exact preview ULID before copying.
func (d *Database) StartImport(ctx context.Context, previewID string) (*ImportJob, error) {
	if err := checkID(previewID); err != nil {
		return nil, err
	}
	return databaseCall[ImportJob](ctx, d, http.MethodPost, nil, struct {
		PreviewID string `json:"previewId"`
	}{previewID}, "import", "start")
}

// CancelImport requests cancellation while retaining the producer's job state.
func (d *Database) CancelImport(ctx context.Context) (*ImportJob, error) {
	return databaseCall[ImportJob](ctx, d, http.MethodPost, nil, nil, "import", "cancel")
}

// ResumeImport resumes a retryable job using its retained staged source.
func (d *Database) ResumeImport(ctx context.Context) (*ImportJob, error) {
	return databaseCall[ImportJob](ctx, d, http.MethodPost, nil, nil, "import", "resume")
}
