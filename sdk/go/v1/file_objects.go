package v1

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"unicode"
)

const maxFileObjectBytes int64 = 100 << 20

// FileObject is a bucket file's customer-visible metadata.
type FileObject struct {
	ID              string          `json:"id" ulid:"required"`
	Name            string          `json:"name"`
	ContentType     string          `json:"contentType"`
	Size            int64           `json:"size"`
	Sha256          string          `json:"sha256"`
	Metadata        json.RawMessage `json:"metadata"`
	Status          string          `json:"status"`
	UploadExpiresAt string          `json:"uploadExpiresAt"`
	DeleteAfter     *string         `json:"deleteAfter"`
}

// FileSignedUrl is a short-lived storage capability; send its headers exactly and never log the URL.
type FileSignedUrl struct {
	ID        string            `json:"id" ulid:"required"`
	URL       string            `json:"url"`
	ExpiresAt string            `json:"expiresAt"`
	Headers   map[string]string `json:"headers"`
}

type createFileUploadRequest struct {
	Name        string          `json:"name"`
	ContentType string          `json:"contentType"`
	Size        int64           `json:"size"`
	Sha256      string          `json:"sha256"`
	Metadata    json.RawMessage `json:"metadata"`
}

// PutObject streams body into bucketID under key with sql:write, spilling to disk so the whole object is never in memory.
func (c *Client) PutObject(ctx context.Context, project, bucketID, key string, body io.Reader, contentType string) (*FileObject, error) {
	if err := checkID(bucketID); err != nil {
		return nil, err
	}
	if len(key) == 0 || len(key) > 255 || strings.IndexFunc(key, unicode.IsControl) >= 0 {
		return nil, errors.New("terrabase: file key must be 1-255 characters without control characters")
	}
	if contentType == "" {
		contentType = "application/octet-stream"
	}
	spill, err := os.CreateTemp("", "terrascale-upload-*")
	if err != nil {
		return nil, fmt.Errorf("terrabase: spill upload: %w", err)
	}
	name := spill.Name()
	defer os.Remove(name)
	hash := sha256.New()
	size, err := io.Copy(io.MultiWriter(spill, hash), body)
	closeErr := spill.Close()
	if err != nil {
		return nil, fmt.Errorf("terrabase: read upload body: %w", err)
	}
	if closeErr != nil {
		return nil, fmt.Errorf("terrabase: spill upload: %w", closeErr)
	}
	if size < 1 || size > maxFileObjectBytes {
		return nil, fmt.Errorf("terrabase: file size must be 1-%d bytes, got %d", maxFileObjectBytes, size)
	}
	payload, err := os.Open(name)
	if err != nil {
		return nil, fmt.Errorf("terrabase: reopen spill: %w", err)
	}
	defer payload.Close()
	capability, err := projectCall[FileSignedUrl](ctx, c, http.MethodPost, project, nil, createFileUploadRequest{
		Name:        key,
		ContentType: contentType,
		Size:        size,
		Sha256:      base64.StdEncoding.EncodeToString(hash.Sum(nil)),
		Metadata:    json.RawMessage("{}"),
	}, "buckets", bucketID, "files", "upload")
	if err != nil {
		return nil, err
	}
	put, err := http.NewRequestWithContext(ctx, http.MethodPut, capability.URL, payload)
	if err != nil {
		return nil, fmt.Errorf("terrabase: build upload request: %w", err)
	}
	for k, v := range capability.Headers {
		put.Header.Set(k, v)
	}
	c.pin(put.Header)
	put.ContentLength = size
	res, err := c.hc.Do(put)
	if err != nil {
		return nil, fmt.Errorf("terrabase: put object: %w", err)
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode > 299 {
		return nil, fmt.Errorf("terrabase: put object: status %d", res.StatusCode)
	}
	stored, err := projectCall[FileObject](ctx, c, http.MethodPost, project, nil, nil, "buckets", bucketID, "files", capability.ID, "complete")
	if err != nil {
		return nil, err
	}
	if stored.ID != capability.ID {
		return nil, fmt.Errorf("terrabase: complete acknowledged file %s, want %s", stored.ID, capability.ID)
	}
	return stored, nil
}

// GetObject streams a file's bytes by key with sql:read; the caller closes the returned body.
func (c *Client) GetObject(ctx context.Context, project, bucketID, key string) (io.ReadCloser, *FileObject, error) {
	stored, err := c.findObject(ctx, project, bucketID, key)
	if err != nil {
		return nil, nil, err
	}
	capability, err := c.SignURL(ctx, project, bucketID, stored.ID)
	if err != nil {
		return nil, nil, err
	}
	get, err := http.NewRequestWithContext(ctx, http.MethodGet, capability.URL, nil)
	if err != nil {
		return nil, nil, fmt.Errorf("terrabase: build download request: %w", err)
	}
	for k, v := range capability.Headers {
		get.Header.Set(k, v)
	}
	c.pin(get.Header)
	res, err := c.hc.Do(get)
	if err != nil {
		return nil, nil, fmt.Errorf("terrabase: get object: %w", err)
	}
	if res.StatusCode != http.StatusOK {
		defer res.Body.Close()
		return nil, nil, fmt.Errorf("terrabase: get object: status %d", res.StatusCode)
	}
	return res.Body, stored, nil
}

// ListObjects lists a bucket's readable files with sql:read, one bounded page in ID order.
func (c *Client) ListObjects(ctx context.Context, project, bucketID string, opts PageOptions) (*Page[FileObject], error) {
	if err := checkID(bucketID); err != nil {
		return nil, err
	}
	req, err := projectRequest(http.MethodGet, project, bucketAfterQuery(opts.After), "buckets", bucketID, "files")
	if err != nil {
		return nil, err
	}
	var out Page[FileObject]
	if _, err := c.call(ctx, req, nil, &out); err != nil {
		return nil, err
	}
	if out.Items == nil {
		return nil, errors.New("terrabase: files response requires items array")
	}
	return &out, nil
}

// DeleteObject queues a key's cleanup with sql:write; reserved bytes bill until physical deletion.
func (c *Client) DeleteObject(ctx context.Context, project, bucketID, key string) error {
	stored, err := c.findObject(ctx, project, bucketID, key)
	if err != nil {
		return err
	}
	req, err := projectRequest(http.MethodDelete, project, nil, "buckets", bucketID, "files", stored.ID)
	if err != nil {
		return err
	}
	_, err = c.call(ctx, req, nil, nil)
	return err
}

// SignURL issues a 60-second download capability for a file ID with sql:read, like the JS downloadUrl.
func (c *Client) SignURL(ctx context.Context, project, bucketID, fileID string) (*FileSignedUrl, error) {
	if err := checkID(bucketID); err != nil {
		return nil, err
	}
	if err := checkID(fileID); err != nil {
		return nil, err
	}
	return projectCall[FileSignedUrl](ctx, c, http.MethodPost, project, nil, nil, "buckets", bucketID, "files", fileID, "download")
}

func (c *Client) findObject(ctx context.Context, project, bucketID, key string) (*FileObject, error) {
	after := ""
	for {
		page, err := c.ListObjects(ctx, project, bucketID, PageOptions{After: after})
		if err != nil {
			return nil, err
		}
		for i := range page.Items {
			if page.Items[i].Name == key {
				return &page.Items[i], nil
			}
		}
		if page.Next == "" {
			return nil, fmt.Errorf("terrabase: file %q not found in bucket %s", key, bucketID)
		}
		after = page.Next
	}
}
