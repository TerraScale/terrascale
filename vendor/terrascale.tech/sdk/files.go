package sdk

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
)

// FileObject is immutable metadata for one tenant-owned file.
type FileObject struct {
	ID          string  `json:"id"`
	Length      int64   `json:"length"`
	SHA256      string  `json:"sha256"`
	CreatedAt   string  `json:"createdAt"`
	ContentType *string `json:"contentType,omitempty"`
	FileName    *string `json:"fileName,omitempty"`
}

type FileObjectsResponse struct {
	Files []FileObject `json:"files"`
}

// FilesClient provides provider-independent file storage operations.
type FilesClient struct{ c *Client }

func fileBase(database string) (string, error) {
	if database == "" {
		return "", fmt.Errorf("sdk: database id cannot be empty")
	}
	return "/api/v1/databases/" + url.PathEscape(database) + "/files", nil
}

// Upload stores bytes and returns their immutable file metadata.
func (f *FilesClient) Upload(ctx context.Context, database string, content []byte, opts ...RequestOption) (*FileObject, error) {
	base, err := fileBase(database)
	if err != nil {
		return nil, err
	}
	if len(content) > 64<<20 {
		return nil, fmt.Errorf("sdk: file content exceeds the 64 MiB limit")
	}
	var out FileObject
	requestOpts := append([]RequestOption(nil), opts...)
	// The file route has no idempotency contract yet; an ambiguous POST must
	// never be replayed automatically and create a second object.
	requestOpts = append(requestOpts, WithoutIdempotency())
	if err := f.c.do(ctx, http.MethodPost, base, nil, importPayload(content), &out, "files.upload", requestOpts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// List returns the metadata for files owned by one database tenant.
func (f *FilesClient) List(ctx context.Context, database string, opts ...RequestOption) (*FileObjectsResponse, error) {
	base, err := fileBase(database)
	if err != nil {
		return nil, err
	}
	var out FileObjectsResponse
	if err := f.c.do(ctx, http.MethodGet, base, nil, nil, &out, "files.list", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Metadata returns one file's immutable metadata.
func (f *FilesClient) Metadata(ctx context.Context, database, id string, opts ...RequestOption) (*FileObject, error) {
	base, err := fileBase(database)
	if err != nil {
		return nil, err
	}
	if id == "" {
		return nil, fmt.Errorf("sdk: file id cannot be empty")
	}
	var out FileObject
	if err := f.c.do(ctx, http.MethodGet, base+"/"+url.PathEscape(id)+"/metadata", nil, nil, &out, "files.metadata", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Download returns the verified file bytes.
func (f *FilesClient) Download(ctx context.Context, database, id string, opts ...RequestOption) ([]byte, error) {
	base, err := fileBase(database)
	if err != nil {
		return nil, err
	}
	if id == "" {
		return nil, fmt.Errorf("sdk: file id cannot be empty")
	}
	var out binaryResponse
	if err := f.c.do(ctx, http.MethodGet, base+"/"+url.PathEscape(id), nil, nil, &out, "files.download", opts...); err != nil {
		return nil, err
	}
	return []byte(out), nil
}
