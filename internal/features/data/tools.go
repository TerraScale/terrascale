package data

import (
	"context"
	stdjson "encoding/json"
	"encoding/json/jsontext"
	json "encoding/json/v2"

	"terrascale.tech/dashboard/internal/core"
)

type databaseRef struct {
	core.Scope
	DatabaseID string `json:"databaseId" jsonschema:"Database ID from list_databases."`
}

type collectionRef struct {
	databaseRef
	Collection string `json:"collection" jsonschema:"Collection name, for example users or orders."`
}

type documentRef struct {
	collectionRef
	DocumentID string `json:"documentId"`
}

type listDocumentsInput struct {
	collectionRef
	Search string `json:"search,omitempty" jsonschema:"Case-insensitive text matched against document IDs and bodies."`
	Limit  int    `json:"limit,omitempty" jsonschema:"Maximum documents to return, 1 to 100. Defaults to every demo document or 100 live documents."`
	After  string `json:"after,omitempty" jsonschema:"Return documents whose ID sorts after this ID, for paging."`
}

type createDocumentInput struct {
	collectionRef
	DocumentID string         `json:"documentId,omitempty" jsonschema:"Document ID. Generated when omitted."`
	Document   map[string]any `json:"document" jsonschema:"The document body as a JSON object."`
}

type replaceDocumentInput struct {
	documentRef
	Document map[string]any `json:"document" jsonschema:"The new document body as a JSON object."`
}

type patchDocumentInput struct {
	documentRef
	Patch map[string]any `json:"patch" jsonschema:"RFC 7396 JSON merge patch: listed fields replace existing ones, null removes a field, nested objects merge."`
}

type setSchemaInput struct {
	collectionRef
	Schema map[string]any `json:"schema" jsonschema:"Collection schema as returned by get_collection_schema; its name must equal the collection."`
}

type createIndexInput struct {
	collectionRef
	Field string `json:"field" jsonschema:"Document field to index."`
}

type recommendationsInput struct {
	databaseRef
	Collection string `json:"collection,omitempty" jsonschema:"Only list recommendations for this collection."`
}

type applyRecommendationInput struct {
	core.Scope
	RecommendationID string `json:"recommendationId" jsonschema:"Recommendation ID from list_index_recommendations."`
	DatabaseID       string `json:"databaseId,omitempty" jsonschema:"Database that owns the recommendation. Searched across the project when omitted."`
	Collection       string `json:"collection,omitempty"`
}

type sqlInput struct {
	databaseRef
	SQL string `json:"sql" jsonschema:"One read-only SELECT, WITH, or EXPLAIN statement."`
}

type saveQueryInput struct {
	core.Scope
	Name string `json:"name"`
	SQL  string `json:"sql" jsonschema:"One read-only SQL statement."`
}

type savedQueryInput struct {
	core.Scope
	QueryID string `json:"queryId" jsonschema:"Saved query ID from list_saved_queries."`
}

type document struct {
	ID    string `json:"id"`
	Value any    `json:"value" jsonschema:"The document body."`
}

type collectionList struct {
	Collections []string `json:"collections"`
}

type documentList struct {
	Documents []document `json:"documents"`
}

type historyVersionOut struct {
	Revision string `json:"revision"`
	Deleted  bool   `json:"deleted"`
	Value    any    `json:"value,omitempty" jsonschema:"The body at this revision, when the data plane records it."`
}

type documentHistory struct {
	Versions []historyVersionOut `json:"versions" jsonschema:"Recorded versions, oldest first."`
}

type collectionSchema struct {
	Collection string `json:"collection"`
	Schema     any    `json:"schema"`
}

type schemaApplied struct {
	Collection string `json:"collection"`
	Status     string `json:"status"`
}

type indexList struct {
	Collection string      `json:"collection"`
	Indexes    []indexView `json:"indexes"`
}

type indexCreated struct {
	Collection string `json:"collection"`
	Field      string `json:"field"`
	Status     string `json:"status"`
}

type recommendationList struct {
	Recommendations []recommendationView `json:"recommendations"`
}

type indexMigration struct {
	Migration *demoIndexMigration `json:"migration,omitempty" jsonschema:"Index backfill progress; absent when no migration is running."`
}

type sqlRunOutput struct {
	Result any `json:"result" jsonschema:"The query result rows as returned by the data plane."`
}

type savedQueryList struct {
	Queries []savedQueryView `json:"queries"`
}

type deleted struct {
	Deleted bool `json:"deleted"`
}

// rawJSON passes a stored JSON value through tool output unchanged.
func rawJSON(value jsontext.Value) any {
	if len(value) == 0 {
		return nil
	}
	return stdjson.RawMessage(value)
}

func documentOut(view documentView) document {
	return document{ID: view.ID, Value: rawJSON(view.Value)}
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_collections", Title: "List collections", ReadOnly: true,
		Description: "List the collection names in a database.",
	}, func(ctx context.Context, p core.Principal, in databaseRef) (collectionList, error) {
		names, err := service.Collections(ctx, p, in.DatabaseID)
		return collectionList{Collections: names}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "list_documents", Title: "List documents", ReadOnly: true,
		Description: "List a collection's documents ordered by ID, with optional search and paging.",
	}, func(ctx context.Context, p core.Principal, in listDocumentsInput) (documentList, error) {
		views, err := service.Documents(ctx, p, in.DatabaseID, documentQuery{Collection: in.Collection, Search: in.Search, Limit: in.Limit, After: in.After})
		list := documentList{Documents: make([]document, 0, len(views))}
		for _, view := range views {
			list.Documents = append(list.Documents, documentOut(view))
		}
		return list, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_document", Title: "Get document", ReadOnly: true,
		Description: "Read one document by ID.",
	}, func(ctx context.Context, p core.Principal, in documentRef) (document, error) {
		view, err := service.Document(ctx, p, in.DatabaseID, in.Collection, in.DocumentID)
		return documentOut(view), err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_document", Title: "Create document",
		Description: "Create a document in a collection. Fails if the ID already exists. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in createDocumentInput) (documentWrite, error) {
		return writeDocument(ctx, service, p, in.collectionRef, "create", in.DocumentID, in.Document)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "replace_document", Title: "Replace document", Destructive: true, Idempotent: true,
		Description: "Replace an existing document's whole body. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in replaceDocumentInput) (documentWrite, error) {
		return writeDocument(ctx, service, p, in.collectionRef, "replace", in.DocumentID, in.Document)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "patch_document", Title: "Patch document",
		Description: "Merge-patch an existing document (RFC 7396). Requires write access.",
	}, func(ctx context.Context, p core.Principal, in patchDocumentInput) (documentWrite, error) {
		return writeDocument(ctx, service, p, in.collectionRef, "patch", in.DocumentID, in.Patch)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "delete_document", Title: "Delete document", Destructive: true,
		Description: "Delete a document. Its history keeps a deletion marker. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in documentRef) (documentWrite, error) {
		return service.WriteDocument(ctx, p, in.DatabaseID, in.Collection, "delete", in.DocumentID, nil)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_document_history", Title: "Get document history", ReadOnly: true,
		Description: "List a document's recorded versions, oldest first, including deletions.",
	}, func(ctx context.Context, p core.Principal, in documentRef) (documentHistory, error) {
		entries, err := service.History(ctx, p, in.DatabaseID, in.Collection, in.DocumentID)
		history := documentHistory{Versions: make([]historyVersionOut, 0, len(entries))}
		for _, entry := range entries {
			history.Versions = append(history.Versions, historyVersionOut{Revision: entry.Revision, Deleted: entry.Deleted, Value: rawJSON(entry.Value)})
		}
		return history, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_collection_schema", Title: "Get collection schema", ReadOnly: true,
		Description: "Read a collection's schema.",
	}, func(ctx context.Context, p core.Principal, in collectionRef) (collectionSchema, error) {
		schema, err := service.Schema(ctx, p, in.DatabaseID, in.Collection)
		return collectionSchema{Collection: in.Collection, Schema: rawJSON(schema)}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "set_collection_schema", Title: "Set collection schema", Idempotent: true,
		Description: "Apply a collection schema. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in setSchemaInput) (schemaApplied, error) {
		body, err := json.Marshal(in.Schema)
		if err != nil {
			return schemaApplied{}, core.Invalid("schema must be a JSON object")
		}
		return schemaApplied{Collection: in.Collection, Status: "applied"}, service.SetSchema(ctx, p, in.DatabaseID, in.Collection, body)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "list_indexes", Title: "List indexes", ReadOnly: true,
		Description: "List a collection's indexed fields and their build state.",
	}, func(ctx context.Context, p core.Principal, in collectionRef) (indexList, error) {
		indexes, err := service.Indexes(ctx, p, in.DatabaseID, in.Collection)
		return indexList{Collection: in.Collection, Indexes: indexes}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_index", Title: "Create index", Idempotent: true,
		Description: "Index one field of a collection. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in createIndexInput) (indexCreated, error) {
		err := service.CreateIndex(ctx, p, in.DatabaseID, in.Collection, in.Field)
		return indexCreated{Collection: in.Collection, Field: in.Field, Status: "created"}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "list_index_recommendations", Title: "List index recommendations", ReadOnly: true,
		Description: "List suggested indexes for a database and whether each was applied.",
	}, func(ctx context.Context, p core.Principal, in recommendationsInput) (recommendationList, error) {
		recommendations, err := service.Recommendations(ctx, p, in.DatabaseID, in.Collection)
		return recommendationList{Recommendations: recommendations}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "apply_index_recommendation", Title: "Apply index recommendation", Idempotent: true,
		Description: "Build the indexes an index recommendation suggests. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in applyRecommendationInput) (recommendationResult, error) {
		return service.ApplyRecommendation(ctx, p, in.DatabaseID, in.Collection, in.RecommendationID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_index_migration", Title: "Get index migration", ReadOnly: true,
		Description: "Show the index backfill progress for a collection, if one is running.",
	}, func(ctx context.Context, p core.Principal, in collectionRef) (indexMigration, error) {
		migration, err := service.Migration(ctx, p, in.DatabaseID, in.Collection)
		return indexMigration{Migration: migration}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "check_sql", Title: "Check SQL", ReadOnly: true,
		Description: "Validate a read-only SQL statement without running it and suggest indexes for it.",
	}, func(ctx context.Context, p core.Principal, in sqlInput) (sqlAnalysis, error) {
		return service.CheckSQL(ctx, p, in.DatabaseID, in.SQL)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "run_sql", Title: "Run SQL", ReadOnly: true,
		Description: "Run one read-only SQL query against a database. The demo supports SELECT * FROM collection [LIMIT n].",
	}, func(ctx context.Context, p core.Principal, in sqlInput) (sqlRunOutput, error) {
		run, err := service.RunSQL(ctx, p, in.DatabaseID, in.SQL)
		return sqlRunOutput{Result: rawJSON(run.JSON)}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "list_saved_queries", Title: "List saved queries", ReadOnly: true,
		Description: "List the SQL queries saved in a project.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (savedQueryList, error) {
		queries, err := service.SavedQueries(ctx, p)
		return savedQueryList{Queries: queries}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "save_query", Title: "Save query",
		Description: "Save a read-only SQL query for everyone in the project. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in saveQueryInput) (savedQueryView, error) {
		return service.SaveQuery(ctx, p, in.Name, in.SQL)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "delete_saved_query", Title: "Delete saved query", Destructive: true,
		Description: "Delete a saved query from the project. Requires write access.",
	}, func(ctx context.Context, p core.Principal, in savedQueryInput) (deleted, error) {
		err := service.DeleteSavedQuery(ctx, p, in.QueryID)
		return deleted{Deleted: err == nil}, err
	})
}

func writeDocument(ctx context.Context, service Service, p core.Principal, ref collectionRef, operation, id string, body map[string]any) (documentWrite, error) {
	encoded, err := json.Marshal(body)
	if err != nil || body == nil {
		return documentWrite{}, core.Invalid("document must be a JSON object")
	}
	return service.WriteDocument(ctx, p, ref.DatabaseID, ref.Collection, operation, id, encoded)
}
