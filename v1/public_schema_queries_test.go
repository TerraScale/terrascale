package v1_test

import (
	"net/http"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestPublicSchemaContracts(t *testing.T) {
	const schema = `{"type":"object","properties":{"id":{"type":"string"}}}`
	for _, endpoint := range []string{"database", "inferred", "declared", "set", "delete"} {
		t.Run(endpoint, func(t *testing.T) {
			method, path, body, reply := http.MethodGet, "/schema", "", `{"items":[{"name":"users","documents":9007199254740993,"bytes":12}]}`
			switch endpoint {
			case "inferred":
				path, reply = "/schema/users", `{"collection":"users","documents":2,"sampledDocuments":2,"fields":[{"path":"email","present":2,"types":{"string":2}}],"uniqueConstraints":[]}`
			case "declared", "set":
				path, reply = "/collections/users/schema", `{"schema":`+schema+`,"dialect":"https://json-schema.org/draft/2020-12/schema"}`
				if endpoint == "set" {
					method, body = http.MethodPut, schema
				}
			case "delete":
				method, path, reply = http.MethodDelete, "/collections/users/schema", `{"schema":null}`
			}
			d := parityClient(t, method, parityDatabase+path, "", body, reply, http.StatusOK).Database(parityProject, "main")
			switch endpoint {
			case "database":
				out, err := d.Schema(t.Context())
				if err != nil || len(out) != 1 || out[0].Documents != 9007199254740993 {
					t.Fatalf("schema = %+v, %v", out, err)
				}
			case "inferred":
				out, err := d.CollectionSchema(t.Context(), "users")
				if err != nil || out.Fields[0].Types["string"] != 2 || out.SampledDocuments != 2 {
					t.Fatalf("inferred = %+v, %v", out, err)
				}
			default:
				var out *tb.DeclaredCollectionSchema
				var err error
				col := d.Collection("users")
				switch endpoint {
				case "set":
					out, err = col.SetSchema(t.Context(), map[string]any{"type": "object", "properties": map[string]any{"id": map[string]string{"type": "string"}}})
				case "delete":
					out, err = col.DeleteSchema(t.Context())
				default:
					out, err = col.Schema(t.Context())
				}
				if err != nil {
					t.Fatal(err)
				}
				if endpoint == "delete" {
					if string(out.Schema) != "null" || out.Dialect != "" {
						t.Fatalf("removed = %+v", out)
					}
				} else if string(out.Schema) != schema || out.Dialect == "" {
					t.Fatalf("declared = %+v", out)
				}
			}
		})
	}
}

func TestPublicSavedQueryContracts(t *testing.T) {
	const saved = `{"id":"` + parityID + `","name":"Recent users","sql":"SELECT * FROM users LIMIT 1","createdAt":"2026-10-22T00:00:00Z","updatedAt":null}`
	const body = `{"name":"Recent users","sql":"SELECT * FROM users LIMIT 1"}`
	for _, endpoint := range []string{"create", "list", "get", "update", "delete", "shapes"} {
		t.Run(endpoint, func(t *testing.T) {
			method, path, query, in, reply, status := http.MethodGet, "/v1/projects/"+parityProject+"/saved-queries", "", "", saved, http.StatusOK
			switch endpoint {
			case "create":
				method, in, status = http.MethodPost, body, http.StatusCreated
			case "list":
				query, reply = "after=opaque%2B%2F%3D&limit=2", `{"items":[`+saved+`],"next":"next+/="}`
			case "get", "update", "delete":
				path += "/" + parityID
				if endpoint == "update" {
					method, in = http.MethodPut, body
				}
				if endpoint == "delete" {
					method, reply, status = http.MethodDelete, "", http.StatusNoContent
				}
			case "shapes":
				path, reply = parityDatabase+"/query-shapes", `{"items":[{"id":"opaque-shape","collection":"users","shape":"email:eq","queries":9007199254740993,"totalLatencyMs":1.5,"maxLatencyMs":1.5,"rowsScanned":2,"lastSeenAt":"2026-10-22T00:00:00Z"}]}`
			}
			c := parityClient(t, method, path, query, in, reply, status)
			request := tb.SavedQueryRequest{Name: "Recent users", SQL: "SELECT * FROM users LIMIT 1"}
			var out *tb.SavedQuery
			var err error
			switch endpoint {
			case "create":
				out, err = c.CreateSavedQuery(t.Context(), parityProject, request)
			case "update":
				out, err = c.UpdateSavedQuery(t.Context(), parityProject, parityID, request)
			case "get":
				out, err = c.SavedQuery(t.Context(), parityProject, parityID)
			case "delete":
				if err := c.DeleteSavedQuery(t.Context(), parityProject, parityID); err != nil {
					t.Fatal(err)
				}
				return
			case "list":
				page, err := c.SavedQueries(t.Context(), parityProject, tb.PageOptions{After: "opaque+/=", Limit: 2})
				if err != nil || len(page.Items) != 1 || page.Next != "next+/=" {
					t.Fatalf("page = %+v, %v", page, err)
				}
				return
			case "shapes":
				shapes, err := c.Database(parityProject, "main").QueryShapes(t.Context())
				if err != nil || len(shapes) != 1 || shapes[0].Queries != 9007199254740993 || shapes[0].MaxLatencyMS != 1.5 {
					t.Fatalf("shapes = %+v, %v", shapes, err)
				}
				return
			}
			if err != nil || out.ID != parityID || out.UpdatedAt != nil || out.CreatedAt.IsZero() {
				t.Fatalf("saved = %+v, %v", out, err)
			}
		})
	}
}
