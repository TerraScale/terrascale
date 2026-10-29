package v1

import (
	"context"
	"net/http"
	"net/url"

	"terrascale.tech/sdk/internal/entityid"
)

// PlatformDatabase is a platform database exposed to platform admins.
type PlatformDatabase struct {
	Name           string   `json:"name"`
	ID             string   `json:"id" ulid:"required"`
	HomeRegion     string   `json:"homeRegion"`
	ReplicaRegions []string `json:"replicaRegions"`
	AllRegions     bool     `json:"allRegions"`
}

// PlatformDatabases lists platform databases. The client must carry a ts-auth
// user token with platform_role=admin; project tokens do not grant this access.
func (c *Client) PlatformDatabases(ctx context.Context) ([]PlatformDatabase, error) {
	out, err := publicCall[struct {
		Items []PlatformDatabase `json:"items"`
	}](ctx, c, request{method: http.MethodGet, path: []string{"v1", "platform", "databases"}, replaySafe: true}, nil)
	if err != nil {
		return nil, err
	}
	return out.Items, nil
}

func platformRequest(database string, query url.Values, extra ...string) (request, error) {
	if err := checkName("database", database); err != nil {
		return request{}, err
	}
	return request{method: http.MethodGet, path: append([]string{"v1", "platform", "databases", database, "collections"}, extra...), query: query, replaySafe: true}, nil
}

// PlatformCollections lists up to 200 collections; requires platform admin.
func (c *Client) PlatformCollections(ctx context.Context, database string) (*CollectionPage, error) {
	req, err := platformRequest(database, nil)
	if err != nil {
		return nil, err
	}
	return publicCall[CollectionPage](ctx, c, req, nil)
}

// PlatformDocuments lists server-redacted documents with cursor pagination.
func (c *Client) PlatformDocuments(ctx context.Context, database, collection string, opts ListOptions) (*QueryResult, error) {
	if err := checkName("collection", collection); err != nil {
		return nil, err
	}
	req, err := platformRequest(database, opts.query(), collection, "documents")
	if err != nil {
		return nil, err
	}
	return c.page(ctx, req, nil)
}

// PlatformDocument reads a server-redacted document by raw customer key.
func (c *Client) PlatformDocument(ctx context.Context, database, collection, id string) (*Document, error) {
	if err := checkName("collection", collection); err != nil {
		return nil, err
	}
	if err := entityid.DocumentKey(id); err != nil {
		return nil, err
	}
	req, err := platformRequest(database, url.Values{"id": {id}}, collection, "documents", "by-key")
	if err != nil {
		return nil, err
	}
	doc, err := publicCall[Document](ctx, c, req, nil)
	if err != nil {
		return nil, err
	}
	if err := entityid.DocumentKey(doc.ID); err != nil {
		return nil, err
	}
	return doc, nil
}
