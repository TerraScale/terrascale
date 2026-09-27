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
		core.WriteJSON(w, http.StatusOK, docs)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/documents", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
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
		if !deps.Store.Can(p, core.Organization.CanWrite) {
			writeError(w, core.ErrForbidden)
			return
		}
		collection, operation := strings.TrimSpace(r.FormValue("collection")), strings.TrimSpace(r.FormValue("operation"))
		submittedID := strings.TrimSpace(r.FormValue("id"))
		body := []byte(r.FormValue("document"))
		// failed shows the error in the form the post came from, refilled.
		failed := func(status int, message string) {
			formFailed(w, r, deps, db.ID, "documents", status, message, func(d *pageData) {
				switch operation {
				case "create":
					d.Dialog, d.NewDocumentID, d.NewDocument = "add-document", submittedID, string(body)
				case "replace", "patch":
					d.Dialog, d.DocumentID, d.EditJSON, d.EditOperation = "edit-document", submittedID, string(body), operation
				}
			})
		}
		if !validCollection(collection) {
			failed(http.StatusBadRequest, "Enter a collection name without slashes or line breaks.")
			return
		}
		if len(submittedID) > 256 {
			failed(http.StatusBadRequest, "Document IDs are at most 256 characters.")
			return
		}
		id := submittedID
		if operation == "create" && id == "" {
			id = core.NewID("doc")
		}
		if id == "" || !slices.Contains([]string{"create", "replace", "patch", "delete"}, operation) {
			failed(http.StatusBadRequest, "Choose a document and an operation.")
			return
		}
		if operation != "delete" && (len(body) == 0 || len(body) > maxDocumentBytes || !jsontext.Value(body).IsValid()) {
			failed(http.StatusBadRequest, "Enter a valid JSON document of 1 MiB or less.")
			return
		}
		if operation == "patch" && jsontext.Value(body).Kind() != '{' {
			failed(http.StatusBadRequest, "A patch must be a JSON object; its fields are merged into the document.")
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
			status, message := documentFailure(err, id, collection)
			failed(status, message)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, sectionPath(db.ID, "documents", url.Values{"collection": {collection}}), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, map[string]string{"id": id, "operation": operation})
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
			core.WriteJSON(w, http.StatusOK, jsontext.Value(body))
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
		core.WriteJSON(w, http.StatusOK, schema)
	})
	mux.HandleFunc("POST /api/v1/dashboard/schema", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
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
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		collection, body := strings.TrimSpace(r.FormValue("collection")), []byte(r.FormValue("schema"))
		failed := func(status int, message string) {
			formFailed(w, r, deps, db.ID, "schema", status, message, func(d *pageData) { d.Schema = string(body) })
		}
		schema, problem := parseSchema(collection, body)
		if problem != "" {
			failed(http.StatusBadRequest, problem)
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
			status, message := failure(err, "The data plane rejected the schema")
			failed(status, message)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, sectionPath(db.ID, "schema", url.Values{"collection": {collection}}), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, map[string]string{"collection": collection, "status": "applied"})
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
			core.WriteJSON(w, http.StatusOK, db.Indexes[collection])
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
		core.WriteJSON(w, http.StatusOK, page.Indexes)
	})
	mux.HandleFunc("POST /api/v1/dashboard/indexes/recommendations/apply", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 8<<10)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, _, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		requestedDatabaseID := strings.TrimSpace(r.FormValue("databaseId"))
		requestedCollection := strings.TrimSpace(r.FormValue("collection"))
		recommendationID := strings.TrimSpace(r.FormValue("recommendationId"))
		if (requestedCollection != "" && !validCollection(requestedCollection)) || recommendationID == "" || len(recommendationID) > 128 {
			writeError(w, core.ErrInvalid)
			return
		}
		if !deps.Demo {
			http.Error(w, "index recommendations are only available in the in-memory demo", http.StatusNotImplemented)
			return
		}
		status := "applied"
		resolvedDatabaseID, resolvedCollection := "", ""
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
				return core.ErrForbidden
			}
			var current core.Database
			recommendationIndex, matches := -1, 0
			for databaseID, database := range project.Databases {
				if database.ProjectID != project.ID || (requestedDatabaseID != "" && databaseID != requestedDatabaseID) {
					continue
				}
				for i, recommendation := range database.IndexRecommendations {
					if recommendation.ID != recommendationID || recommendation.DatabaseID != databaseID || (requestedCollection != "" && recommendation.Collection != requestedCollection) {
						continue
					}
					matches++
					resolvedDatabaseID, resolvedCollection = databaseID, recommendation.Collection
					current, recommendationIndex = database, i
				}
			}
			if matches == 0 {
				return core.ErrNotFound
			}
			if matches > 1 {
				return core.ErrConflict
			}
			if !validCollection(resolvedCollection) {
				return core.ErrInvalid
			}
			recommendation := current.IndexRecommendations[recommendationIndex]
			if recommendation.State == "applied" {
				status = "already-applied"
				return nil
			}
			if recommendation.State != "recommended" || len(recommendation.Fields) == 0 {
				return core.ErrConflict
			}
			for _, field := range recommendation.Fields {
				if field == "" || len(field) > 128 || strings.ContainsAny(field, "/\\\x00\n\r") {
					return core.ErrInvalid
				}
			}
			if current.Indexes == nil {
				current.Indexes = make(map[string][]string)
			}
			for _, field := range recommendation.Fields {
				if slices.Contains(current.Indexes[resolvedCollection], field) {
					continue
				}
				current.Indexes[resolvedCollection] = append(current.Indexes[resolvedCollection], field)
				current.IndexBuilds = append(current.IndexBuilds, core.IndexBuild{ID: core.NewID("index"), Collection: resolvedCollection, Field: field, Status: "building"})
			}
			current.IndexRecommendations[recommendationIndex].State = "applied"
			project.Databases[resolvedDatabaseID] = current
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			status, message := failure(err, "The recommendation could not be applied")
			if errors.Is(err, core.ErrNotFound) || errors.Is(err, core.ErrConflict) {
				message = "This recommendation is no longer available to apply."
			}
			formFailed(w, r, deps, requestedDatabaseID, "indexes", status, message, func(d *pageData) { d.IndexView = "recommendations" })
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, sectionPath(resolvedDatabaseID, "indexes", url.Values{"view": {"recommendations"}, "collection": {resolvedCollection}}), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, map[string]string{"recommendationId": recommendationID, "status": status})
	})
	mux.HandleFunc("POST /api/v1/dashboard/indexes", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
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
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		collection, field := strings.TrimSpace(r.FormValue("collection")), strings.TrimSpace(r.FormValue("field"))
		failed := func(status int, message string) {
			formFailed(w, r, deps, db.ID, "indexes", status, message, func(d *pageData) { d.IndexField = field })
		}
		if !validCollection(collection) || field == "" || len(field) > 128 {
			failed(http.StatusBadRequest, "Enter a field name of at most 128 characters.")
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
			status, message := failure(err, "The data plane could not create the index")
			failed(status, message)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, sectionPath(db.ID, "indexes", url.Values{"collection": {collection}}), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, map[string]string{"field": field, "status": "created"})
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

func client(deps core.Deps, p core.Principal) (*sdk.Client, error) {
	if deps.SDK == nil {
		return nil, errors.New("live data plane unavailable")
	}
	return deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
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
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, "invalid input", http.StatusBadRequest)
	default:
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	}
}

// failure maps an error from a change to the status and message its form
// shows. Any other error comes from the data plane and keeps its explanation
// after action.
func failure(err error, action string) (int, string) {
	switch {
	case errors.Is(err, core.ErrForbidden):
		return http.StatusForbidden, "Your role cannot make this change. Ask a project manager."
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, "This item no longer exists. Reload the page and try again."
	case errors.Is(err, core.ErrConflict):
		return http.StatusConflict, "This change conflicts with the current data. Reload the page and try again."
	case errors.Is(err, core.ErrInvalid):
		return http.StatusBadRequest, "Check the values you entered."
	default:
		return http.StatusBadGateway, action + ": " + err.Error()
	}
}

// documentFailure names the document in the messages a document form shows.
func documentFailure(err error, id, collection string) (int, string) {
	switch {
	case errors.Is(err, core.ErrConflict):
		return http.StatusConflict, "A document with ID " + id + " already exists in " + collection + ". Choose another ID or edit that document."
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, "Document " + id + " no longer exists in " + collection + "."
	case errors.Is(err, core.ErrInvalid):
		return http.StatusBadRequest, "A patch must be a JSON object; its fields are merged into the document."
	default:
		return failure(err, "The data plane rejected the change")
	}
}

// parseSchema decodes a collection schema in the terrascale.schema.json
// format and returns the reason it cannot be applied, if any.
func parseSchema(collection string, body []byte) (sdk.CollectionSchema, string) {
	var schema sdk.CollectionSchema
	switch {
	case !validCollection(collection):
		return schema, "Enter a collection name without slashes or line breaks."
	case len(body) == 0 || len(body) > 128<<10:
		return schema, "Enter a schema of at most 128 KiB."
	}
	if err := json.Unmarshal(body, &schema); err != nil {
		return schema, "The schema is not valid JSON for a collection schema: " + strings.TrimPrefix(err.Error(), "json: ")
	}
	if schema.Name != collection {
		return schema, `The schema "name" must be "` + collection + `", the collection it applies to.`
	}
	if err := schema.Validate(); err != nil {
		return schema, "The schema is invalid: " + strings.TrimPrefix(err.Error(), "sdk: ") + "."
	}
	return schema, ""
}
