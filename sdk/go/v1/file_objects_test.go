package v1_test

import (
	"crypto/sha256"
	"encoding/base64"
	"io"
	"net/http"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestFileObjectRoundTrip(t *testing.T) {
	const (
		fileID   = "01JA0000000000000000000003"
		fileURL  = "https://store.test/objects/hello.txt"
		expires  = "2026-01-01T00:00:00.000Z"
		fileBody = "hello"
	)
	digest := base64.StdEncoding.EncodeToString(sum256(fileBody))
	fileBase := "/v1/projects/" + bucketProject + "/buckets/" + bucketID + "/files"
	uploadBody := `{"name":"hello.txt","contentType":"text/plain","size":5,"sha256":"` + digest + `","metadata":{}}`
	signedJSON := `{"id":"` + fileID + `","url":"` + fileURL + `","expiresAt":"` + expires + `","headers":{"content-type":"text/plain","content-length":"5","x-amz-checksum-sha256":"` + digest + `"}}`
	downloadJSON := `{"id":"` + fileID + `","url":"` + fileURL + `","expiresAt":"` + expires + `","headers":{}}`
	fileJSON := `{"id":"` + fileID + `","name":"hello.txt","contentType":"text/plain","size":5,"sha256":"` + digest + `","metadata":{"kind":"fixture"},"status":"ready","uploadExpiresAt":"` + expires + `","deleteAfter":null}`
	pageJSON := `{"items":[` + fileJSON + `],"next":null}`
	c := bucketClient(t, []bucketFixture{
		{name: "create upload", method: http.MethodPost, path: fileBase + "/upload", body: uploadBody, status: http.StatusCreated, reply: signedJSON},
		{name: "put bytes", method: http.MethodPut, path: "/objects/hello.txt", body: fileBody, status: http.StatusOK},
		{name: "complete", method: http.MethodPost, path: fileBase + "/" + fileID + "/complete", status: http.StatusOK, reply: fileJSON},
		{name: "list", method: http.MethodGet, path: fileBase, status: http.StatusOK, reply: pageJSON},
		{name: "locate", method: http.MethodGet, path: fileBase, status: http.StatusOK, reply: pageJSON},
		{name: "download url", method: http.MethodPost, path: fileBase + "/" + fileID + "/download", status: http.StatusOK, reply: downloadJSON},
		{name: "get bytes", method: http.MethodGet, path: "/objects/hello.txt", status: http.StatusOK, reply: fileBody},
		{name: "sign", method: http.MethodPost, path: fileBase + "/" + fileID + "/download", status: http.StatusOK, reply: downloadJSON},
		{name: "locate for delete", method: http.MethodGet, path: fileBase, status: http.StatusOK, reply: pageJSON},
		{name: "delete", method: http.MethodDelete, path: fileBase + "/" + fileID, status: http.StatusNoContent},
	})
	ctx := t.Context()
	stored, err := c.PutObject(ctx, bucketProject, bucketID, "hello.txt", strings.NewReader(fileBody), "text/plain")
	if err != nil || stored.ID != fileID || stored.Name != "hello.txt" || stored.Size != 5 || stored.Status != "ready" {
		t.Fatalf("put = %+v, %v", stored, err)
	}
	page, err := c.ListObjects(ctx, bucketProject, bucketID, tb.PageOptions{})
	if err != nil || len(page.Items) != 1 || page.Items[0].ID != fileID || page.Next != "" {
		t.Fatalf("list = %+v, %v", page, err)
	}
	body, got, err := c.GetObject(ctx, bucketProject, bucketID, "hello.txt")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	bytes, readErr := io.ReadAll(body)
	closeErr := body.Close()
	if readErr != nil || closeErr != nil || string(bytes) != fileBody || got.ID != fileID {
		t.Fatalf("get = %q, %+v, %v, %v", bytes, got, readErr, closeErr)
	}
	signed, err := c.SignURL(ctx, bucketProject, bucketID, fileID)
	if err != nil || signed.URL != fileURL || signed.ID != fileID || signed.ExpiresAt != expires || len(signed.Headers) != 0 {
		t.Fatalf("sign = %+v, %v", signed, err)
	}
	if err := c.DeleteObject(ctx, bucketProject, bucketID, "hello.txt"); err != nil {
		t.Fatalf("delete: %v", err)
	}
}

func sum256(data string) []byte {
	sum := sha256.Sum256([]byte(data))
	return sum[:]
}
