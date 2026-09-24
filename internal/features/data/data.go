package data

import (
	stdjson "encoding/json"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"errors"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strings"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

const maxDocumentBytes = 1 << 20

var selectPattern = regexp.MustCompile(`(?i)^select\s+\*\s+from\s+([a-z][a-z0-9_-]*)(?:\s+limit\s+([0-9]{1,3}))?\s*;?$`)

type documentView struct {
	ID    string         `json:"id"`
	Value jsontext.Value `json:"value"`
}

func Mount(mux *http.ServeMux, deps core.Deps) {
	mountPages(mux, deps)
	mountQueryRoutes(mux, deps)
	mux.HandleFunc("GET /api/v1/dashboard/data/documents", func(w http.ResponseWriter, r *http.Request) {
		p, db, err := selectedDB(deps, r, r.URL.Query().Get("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		collection := strings.TrimSpace(r.URL.Query().Get("collection"))
		if !validCollection(collection) {
			http.Error(w, "invalid collection", http.StatusBadRequest)
			return
		}
		var docs []documentView
		if deps.Demo {
			for id, body := range db.Documents[collection] {
				docs = append(docs, documentView{ID: id, Value: jsontext.Value(body).Clone()})
			}
			slices.SortFunc(docs, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
		} else {
			client, err := client(deps, p)
			if err != nil {
				writeError(w, err)
				return
			}
			page, err := client.Documents.List(r.Context(), db.Name, collection, 100, "")
			if err != nil {
				http.Error(w, "document list failed: "+err.Error(), http.StatusBadGateway)
				return
			}
			for _, row := range page.Documents {
				docs = append(docs, documentView{ID: row.ID, Value: jsontext.Value(row.Value)})
			}
		}
		writeJSON(w, http.StatusOK, docs)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/documents", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, maxDocumentBytes+4096)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid document form", http.StatusBadRequest)
			return
		}
		p, db, err := selectedDB(deps, r, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		if !canWrite(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		collection, operation := strings.TrimSpace(r.FormValue("collection")), strings.TrimSpace(r.FormValue("operation"))
		id := strings.TrimSpace(r.FormValue("id"))
		if !validCollection(collection) || (id != "" && len(id) > 256) {
			http.Error(w, "invalid collection or document ID", http.StatusBadRequest)
			return
		}
		if operation == "create" && id == "" {
			id = core.NewID("doc")
		}
		if id == "" || !slices.Contains([]string{"create", "replace", "patch", "delete"}, operation) {
			http.Error(w, "invalid document operation", http.StatusBadRequest)
			return
		}
		body := []byte(r.FormValue("document"))
		if operation != "delete" && (len(body) == 0 || len(body) > maxDocumentBytes || !jsontext.Value(body).IsValid()) {
			http.Error(w, "document must be valid JSON within 1 MiB", http.StatusBadRequest)
			return
		}
		if deps.Demo {
			err = deps.Store.UpdateAs(p, func(a *core.Account) error {
				project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
				if err != nil {
					return err
				}
				if !a.Organizations[p.OrganizationID].CanWrite(p.UserID) {
					return core.ErrForbidden
				}
				current, ok := project.Databases[db.ID]
				if !ok || current.ProjectID != project.ID {
					return core.ErrNotFound
				}
				if current.Documents == nil {
					current.Documents = make(map[string]map[string][]byte)
				}
				docs := current.Documents[collection]
				if docs == nil {
					docs = make(map[string][]byte)
				}
				old, exists := docs[id]
				switch operation {
				case "create":
					if exists {
						return core.ErrConflict
					}
					docs[id] = slices.Clone(body)
				case "replace":
					if !exists {
						return core.ErrNotFound
					}
					docs[id] = slices.Clone(body)
				case "patch":
					if !exists {
						return core.ErrNotFound
					}
					merged, err := mergePatch(old, body)
					if err != nil {
						return core.ErrInvalid
					}
					docs[id] = merged
				case "delete":
					if !exists {
						return core.ErrNotFound
					}
					delete(docs, id)
				}
				if current.DocumentHistory == nil {
					current.DocumentHistory = make(map[string]map[string][][]byte)
				}
				if current.DocumentHistory[collection] == nil {
					current.DocumentHistory[collection] = make(map[string][][]byte)
				}
				version := docs[id]
				if operation == "delete" {
					version = []byte("null")
				}
				current.DocumentHistory[collection][id] = append(current.DocumentHistory[collection][id], slices.Clone(version))
				current.Documents[collection] = docs
				project.Databases[db.ID] = current
				org := a.Organizations[p.OrganizationID]
				org.Projects[p.ProjectID] = project
				a.Organizations[p.OrganizationID] = org
				return nil
			})
		} else {
			var sdkClient *sdk.Client
			sdkClient, err = client(deps, p)
			if err == nil {
				opts := []sdk.RequestOption{sdk.WithIdempotencyKey(core.NewID("docop"))}
				switch operation {
				case "create":
					_, err = sdkClient.Documents.Create(r.Context(), db.Name, collection, id, stdjson.RawMessage(body), opts...)
				case "replace":
					_, err = sdkClient.Documents.Put(r.Context(), db.Name, collection, id, stdjson.RawMessage(body), opts...)
				case "patch":
					_, err = sdkClient.Documents.Patch(r.Context(), db.Name, collection, id, stdjson.RawMessage(body), opts...)
				case "delete":
					err = sdkClient.Documents.Delete(r.Context(), db.Name, collection, id, opts...)
				}
			}
		}
		if err != nil {
			if deps.Demo {
				writeError(w, err)
			} else {
				http.Error(w, "document operation failed: "+err.Error(), http.StatusBadGateway)
			}
			return
		}
		if wantsHTML(r) {
			http.Redirect(w, r, "/data?view=table&databaseId="+url.QueryEscape(db.ID)+"&collection="+url.QueryEscape(collection), http.StatusSeeOther)
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"id": id, "operation": operation})
	})
	mux.HandleFunc("GET /api/v1/dashboard/schema", func(w http.ResponseWriter, r *http.Request) {
		p, db, err := selectedDB(deps, r, r.URL.Query().Get("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		collection := strings.TrimSpace(r.URL.Query().Get("collection"))
		if !validCollection(collection) {
			http.Error(w, "invalid collection", http.StatusBadRequest)
			return
		}
		if deps.Demo {
			body, ok := db.Schemas[collection]
			if !ok {
				writeError(w, core.ErrNotFound)
				return
			}
			writeJSON(w, http.StatusOK, jsontext.Value(body))
			return
		}
		client, err := client(deps, p)
		if err != nil {
			writeError(w, err)
			return
		}
		schema, err := client.Databases.GetCollectionSchema(r.Context(), db.Name, collection)
		if err != nil {
			http.Error(w, "schema read failed: "+err.Error(), http.StatusBadGateway)
			return
		}
		writeJSON(w, http.StatusOK, schema)
	})
	mux.HandleFunc("POST /api/v1/dashboard/schema", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 256<<10)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, db, err := selectedDB(deps, r, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		collection, body := strings.TrimSpace(r.FormValue("collection")), []byte(r.FormValue("schema"))
		var schema sdk.CollectionSchema
		if !validCollection(collection) || len(body) == 0 || len(body) > 128<<10 || json.Unmarshal(body, &schema) != nil || schema.Name != collection || schema.Validate() != nil {
			http.Error(w, "schema must be valid JSON for the selected collection", http.StatusBadRequest)
			return
		}
		if deps.Demo {
			err = deps.Store.UpdateAs(p, func(a *core.Account) error {
				project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
				if err != nil {
					return err
				}
				if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
					return core.ErrForbidden
				}
				current, ok := project.Databases[db.ID]
				if !ok {
					return core.ErrNotFound
				}
				if current.Schemas == nil {
					current.Schemas = make(map[string][]byte)
				}
				current.Schemas[collection] = slices.Clone(body)
				project.Databases[db.ID] = current
				org := a.Organizations[p.OrganizationID]
				org.Projects[p.ProjectID] = project
				a.Organizations[p.OrganizationID] = org
				return nil
			})
		} else {
			var sdkClient *sdk.Client
			sdkClient, err = client(deps, p)
			if err == nil {
				current, getErr := sdkClient.Databases.GetCollectionSchema(r.Context(), db.Name, collection)
				if getErr != nil {
					err = getErr
				} else {
					_, err = sdkClient.Databases.PutCollectionSchema(r.Context(), db.Name, collection, schema, sdk.WithIdempotencyKey(core.NewID("schema")), sdk.WithSchemaGeneration(current.Generation))
				}
			}
		}
		if err != nil {
			if deps.Demo {
				writeError(w, err)
			} else {
				http.Error(w, "schema apply failed: "+err.Error(), http.StatusBadGateway)
			}
			return
		}
		if wantsHTML(r) {
			http.Redirect(w, r, "/schema?databaseId="+url.QueryEscape(db.ID)+"&collection="+url.QueryEscape(collection), http.StatusSeeOther)
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"collection": collection, "status": "applied"})
	})
	mux.HandleFunc("GET /api/v1/dashboard/indexes", func(w http.ResponseWriter, r *http.Request) {
		p, db, err := selectedDB(deps, r, r.URL.Query().Get("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		collection := strings.TrimSpace(r.URL.Query().Get("collection"))
		if !validCollection(collection) {
			http.Error(w, "invalid collection", http.StatusBadRequest)
			return
		}
		if deps.Demo {
			writeJSON(w, http.StatusOK, db.Indexes[collection])
			return
		}
		client, err := client(deps, p)
		if err != nil {
			writeError(w, err)
			return
		}
		page, err := client.Databases.ListIndexes(r.Context(), db.Name, collection, sdk.PageRequest{})
		if err != nil {
			http.Error(w, "index list failed: "+err.Error(), http.StatusBadGateway)
			return
		}
		writeJSON(w, http.StatusOK, page.Indexes)
	})
	mux.HandleFunc("POST /api/v1/dashboard/indexes", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, db, err := selectedDB(deps, r, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		collection, field := strings.TrimSpace(r.FormValue("collection")), strings.TrimSpace(r.FormValue("field"))
		if !validCollection(collection) || field == "" || len(field) > 128 {
			http.Error(w, "invalid collection or field", http.StatusBadRequest)
			return
		}
		if deps.Demo {
			err = deps.Store.UpdateAs(p, func(a *core.Account) error {
				project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
				if err != nil {
					return err
				}
				if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
					return core.ErrForbidden
				}
				current, ok := project.Databases[db.ID]
				if !ok {
					return core.ErrNotFound
				}
				if current.Indexes == nil {
					current.Indexes = make(map[string][]string)
				}
				if !slices.Contains(current.Indexes[collection], field) {
					current.Indexes[collection] = append(current.Indexes[collection], field)
				}
				project.Databases[db.ID] = current
				org := a.Organizations[p.OrganizationID]
				org.Projects[p.ProjectID] = project
				a.Organizations[p.OrganizationID] = org
				return nil
			})
		} else {
			var sdkClient *sdk.Client
			sdkClient, err = client(deps, p)
			if err == nil {
				_, err = sdkClient.Databases.CreateIndex(r.Context(), db.Name, collection, sdk.CreateIndexRequest{Field: field}, sdk.WithIdempotencyKey(core.NewID("index")))
			}
		}
		if err != nil {
			if deps.Demo {
				writeError(w, err)
			} else {
				http.Error(w, "index create failed: "+err.Error(), http.StatusBadGateway)
			}
			return
		}
		if wantsHTML(r) {
			http.Redirect(w, r, "/indexes?databaseId="+url.QueryEscape(db.ID)+"&collection="+url.QueryEscape(collection), http.StatusSeeOther)
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"field": field, "status": "created"})
	})
}

func selectedDB(deps core.Deps, r *http.Request, databaseID string) (core.Principal, core.Database, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Database{}, core.ErrUnauthorized
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Database{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	if err != nil {
		return core.Principal{}, core.Database{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok || db.ProjectID != project.ID {
		return core.Principal{}, core.Database{}, core.ErrNotFound
	}
	return p, db, nil
}

func validCollection(collection string) bool {
	return collection != "" && len(collection) <= 128 && !strings.ContainsAny(collection, "/\\\x00\n\r")
}

func wantsHTML(r *http.Request) bool { return strings.Contains(r.Header.Get("Accept"), "text/html") }

func client(deps core.Deps, p core.Principal) (*sdk.Client, error) {
	if deps.SDK == nil {
		return nil, errors.New("live data plane unavailable")
	}
	return deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
}

func canWrite(deps core.Deps, p core.Principal) bool {
	a, err := deps.Store.SnapshotAs(p)
	return err == nil && a.Organizations[p.OrganizationID].CanWrite(p.UserID)
}

func canManage(deps core.Deps, p core.Principal) bool {
	a, err := deps.Store.SnapshotAs(p)
	return err == nil && a.Organizations[p.OrganizationID].CanManage(p.UserID)
}

func mergePatch(original, patch []byte) ([]byte, error) {
	var changes map[string]jsontext.Value
	if err := json.Unmarshal(patch, &changes); err != nil {
		return nil, err
	}
	if changes == nil {
		return slices.Clone(patch), nil
	}
	var target map[string]jsontext.Value
	if err := json.Unmarshal(original, &target); err != nil || target == nil {
		target = make(map[string]jsontext.Value)
	}
	for key, value := range changes {
		if value.Kind() == 'n' {
			delete(target, key)
			continue
		}
		if value.Kind() == '{' {
			prior := target[key]
			if prior.Kind() != '{' {
				prior = jsontext.Value(`{}`)
			}
			merged, err := mergePatch(prior, value)
			if err != nil {
				return nil, err
			}
			target[key] = merged
			continue
		}
		target[key] = value.Clone()
	}
	return json.Marshal(target)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "authentication required", http.StatusUnauthorized)
	case errors.Is(err, core.ErrForbidden):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, "invalid input", http.StatusBadRequest)
	default:
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	}
}

func writeJSON(w http.ResponseWriter, status int, data any) {
	encoded, err := json.Marshal(data)
	if err != nil {
		http.Error(w, "encode response failed", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_, _ = w.Write(encoded)
}
