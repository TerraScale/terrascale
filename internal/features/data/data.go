package data

import (
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

var selectPattern = regexp.MustCompile(`(?i)^select\s+\*\s+from\s+([a-z][a-z0-9_-]*)(?:\s+limit\s+([0-9]{1,3}))?\s*;?$`)

// Mount owns the database data routes and tools: documents, schemas,
// indexes, SQL, and saved queries.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps}
	mountTools(deps.Tools, service)
	mountPages(mux, deps)
	mountQueryRoutes(mux, deps, service)
	mux.HandleFunc("GET /api/v1/dashboard/data/documents", func(w http.ResponseWriter, r *http.Request) {
		p, ok := principal(w, r, deps)
		if !ok {
			return
		}
		query := r.URL.Query()
		docs, err := service.Documents(r.Context(), p, query.Get("databaseId"), documentQuery{Collection: strings.TrimSpace(query.Get("collection"))})
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, docs)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/documents", func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxDocumentBytes+4096)
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		db, err := service.database(p, r.FormValue("databaseId"))
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
		result, err := service.WriteDocument(r.Context(), p, db.ID, collection, operation, id, body)
		if err != nil {
			status, message := documentFailure(err, id, collection)
			failed(status, message)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, sectionPath(db.ID, "documents", url.Values{"collection": {collection}}), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, result)
	})
	mux.HandleFunc("GET /api/v1/dashboard/schema", func(w http.ResponseWriter, r *http.Request) {
		p, ok := principal(w, r, deps)
		if !ok {
			return
		}
		schema, err := service.Schema(r.Context(), p, r.URL.Query().Get("databaseId"), strings.TrimSpace(r.URL.Query().Get("collection")))
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, schema)
	})
	mux.HandleFunc("POST /api/v1/dashboard/schema", func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, 256<<10)
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		db, err := service.database(p, r.FormValue("databaseId"))
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
		if _, problem := parseSchema(collection, body); problem != "" {
			failed(http.StatusBadRequest, problem)
			return
		}
		if err := service.SetSchema(r.Context(), p, db.ID, collection, body); err != nil {
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
		p, ok := principal(w, r, deps)
		if !ok {
			return
		}
		indexes, err := service.Indexes(r.Context(), p, r.URL.Query().Get("databaseId"), strings.TrimSpace(r.URL.Query().Get("collection")))
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, indexes)
	})
	mux.HandleFunc("POST /api/v1/dashboard/indexes/recommendations/apply", func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, 8<<10)
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		result, err := service.ApplyRecommendation(p, strings.TrimSpace(r.FormValue("databaseId")),
			strings.TrimSpace(r.FormValue("collection")), strings.TrimSpace(r.FormValue("recommendationId")))
		if err != nil {
			writeError(w, err)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, sectionPath(result.DatabaseID, "indexes", url.Values{"view": {"recommendations"}, "collection": {result.Collection}}), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, map[string]string{"recommendationId": result.RecommendationID, "status": result.Status})
	})
	mux.HandleFunc("POST /api/v1/dashboard/indexes", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		db, err := service.database(p, r.FormValue("databaseId"))
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
		if err := service.CreateIndex(r.Context(), p, db.ID, collection, field); err != nil {
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

// principal resolves the signed-in principal with a selected project.
func principal(w http.ResponseWriter, r *http.Request, deps core.Deps) (core.Principal, bool) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		writeError(w, core.ErrUnauthorized)
		return core.Principal{}, false
	}
	return p, true
}

func validCollection(collection string) bool {
	return collection != "" && len(collection) <= 128 && !strings.ContainsAny(collection, "/\\\x00\n\r")
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
	var provider core.ProviderError
	switch {
	case errors.Is(err, core.ErrDataPlane):
		http.Error(w, err.Error(), http.StatusServiceUnavailable)
	case errors.Is(err, errDemoOnly):
		http.Error(w, err.Error(), http.StatusNotImplemented)
	case errors.As(err, &provider):
		http.Error(w, err.Error(), http.StatusBadGateway)
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, err.Error(), http.StatusBadRequest)
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
