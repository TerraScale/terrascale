package data

import (
	"cmp"
	"context"
	stdjson "encoding/json"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"errors"
	"fmt"
	"maps"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
	"terrascale.tech/sdk/v1"
)

const (
	maxDocumentBytes = 1 << 20
	// maxDemoRows is the most rows one in-memory demo query returns.
	maxDemoRows    = 100
	maxSchemaBytes = 128 << 10
	maxListLimit   = 100
)

var errDemoOnly = errors.New("index recommendations are only available in the in-memory demo")

// documentOperations are the writes a document form or tool may request.
var documentOperations = []string{"create", "replace", "patch", "delete"}

type documentView struct {
	ID    string         `json:"id"`
	Value jsontext.Value `json:"value"`
}

type documentWrite struct {
	ID        string `json:"id"`
	Operation string `json:"operation"`
}

type historyEntry struct {
	Revision string         `json:"revision"`
	Deleted  bool           `json:"deleted"`
	Value    jsontext.Value `json:"value,omitempty"`
}

type indexView struct {
	Field             string `json:"field"`
	State             string `json:"state"`
	BackfillRemaining uint64 `json:"backfillRemaining,omitempty"`
}

type recommendationView struct {
	ID         string   `json:"id"`
	Collection string   `json:"collection"`
	Fields     []string `json:"fields"`
	Reason     string   `json:"reason"`
	State      string   `json:"state"`
}

type recommendationResult struct {
	RecommendationID string `json:"recommendationId"`
	Status           string `json:"status" jsonschema:"applied, or already-applied when a previous call applied it."`
	DatabaseID       string `json:"databaseId"`
	Collection       string `json:"collection"`
}

type savedQueryView struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	SQL       string    `json:"sql"`
	CreatedAt time.Time `json:"createdAt"`
}

// documentQuery selects one page of a collection. A zero Limit lists every
// demo document, or the first 100 from the live data plane.
type documentQuery struct {
	Collection string
	Search     string
	Limit      int
	After      string
}

// Service owns documents, schemas, indexes, SQL, and saved queries in a
// principal's selected project. HTTP handlers and MCP tools both call it.
type Service struct {
	deps core.Deps
}

func (s Service) project(ctx context.Context, p core.Principal) (core.Account, core.Project, error) {
	if p.ProjectID == "" {
		return core.Account{}, core.Project{}, core.ErrNotFound
	}
	a, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Account{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return a, project, err
}

func (s Service) database(ctx context.Context, p core.Principal, databaseID string) (core.Database, error) {
	_, project, err := s.project(ctx, p)
	if err != nil {
		return core.Database{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok || db.ProjectID != project.ID {
		return core.Database{}, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	return db, nil
}

// collectionDatabase resolves the database and validates the collection name.
func (s Service) collectionDatabase(ctx context.Context, p core.Principal, databaseID, collection string) (core.Database, error) {
	db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return core.Database{}, err
	}
	if !validCollection(collection) {
		return core.Database{}, core.Invalid("collection names use 1-128 characters without slashes or line breaks")
	}
	return db, nil
}

// updateDatabase commits mutate to one demo database once allow grants the
// principal's role in the selected organization.
func (s Service) updateDatabase(ctx context.Context, p core.Principal, databaseID string, allow func(core.Organization, string) bool, mutate func(*core.Database) error) error {
	return s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !allow(org, p.UserID) {
			return core.ErrForbidden
		}
		current, ok := project.Databases[databaseID]
		if !ok || current.ProjectID != project.ID {
			return core.ErrNotFound
		}
		if err := mutate(&current); err != nil {
			return err
		}
		project.Databases[databaseID] = current
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

// updateProject commits mutate to the selected project once allow grants the
// principal's role in the selected organization.
func (s Service) updateProject(ctx context.Context, p core.Principal, allow func(core.Organization, string) bool, mutate func(*core.Project) error) error {
	return s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !allow(org, p.UserID) {
			return core.ErrForbidden
		}
		if err := mutate(&project); err != nil {
			return err
		}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

// Collections lists the database's collection names.
func (s Service) Collections(ctx context.Context, p core.Principal, databaseID string) ([]string, error) {
	db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return nil, err
	}
	if s.deps.LocalResources() {
		names := slices.Concat(slices.Collect(maps.Keys(db.Documents)), slices.Collect(maps.Keys(db.Schemas)), slices.Collect(maps.Keys(db.Indexes)))
		slices.Sort(names)
		return slices.Compact(names), nil
	}
	client, err := s.deps.Documents(p, db.Name)
	if err != nil {
		return nil, err
	}
	page, err := client.Collections(ctx, v1.ListOptions{Limit: 200})
	if err != nil {
		return nil, core.ProviderError{Action: "collection list", Err: err}
	}
	names := make([]string, 0, len(page.Items))
	for _, item := range page.Items {
		names = append(names, item.Name)
	}
	return names, nil
}

// Documents lists one page of a collection, ordered by ID and filtered by a
// case-insensitive search over IDs and bodies.
func (s Service) Documents(ctx context.Context, p core.Principal, databaseID string, query documentQuery) ([]documentView, error) {
	db, err := s.collectionDatabase(ctx, p, databaseID, query.Collection)
	if err != nil {
		return nil, err
	}
	if query.Limit < 0 || query.Limit > maxListLimit {
		return nil, core.Invalid("limit must be 1 to 100")
	}
	var docs []documentView
	if s.deps.LocalResources() {
		for id, body := range db.Documents[query.Collection] {
			if query.After == "" || id > query.After {
				docs = append(docs, documentView{ID: id, Value: jsontext.Value(body).Clone()})
			}
		}
		slices.SortFunc(docs, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
	} else {
		client, err := s.deps.Documents(p, db.Name)
		if err != nil {
			return nil, err
		}
		page, err := client.Collection(query.Collection).List(ctx, v1.ListOptions{Limit: cmp.Or(query.Limit, maxListLimit), Cursor: query.After})
		if err != nil {
			return nil, core.ProviderError{Action: "document list", Err: err}
		}
		for _, row := range page.Items {
			docs = append(docs, documentView{ID: row.ID, Value: jsontext.Value(row.Document)})
		}
	}
	if search := strings.ToLower(strings.TrimSpace(query.Search)); search != "" {
		docs = slices.DeleteFunc(docs, func(document documentView) bool {
			return !strings.Contains(strings.ToLower(document.ID+" "+document.Value.String()), search)
		})
	}
	if query.Limit > 0 && len(docs) > query.Limit {
		docs = docs[:query.Limit]
	}
	return docs, nil
}

// Document reads one document.
func (s Service) Document(ctx context.Context, p core.Principal, databaseID, collection, id string) (documentView, error) {
	db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return documentView{}, err
	}
	if s.deps.LocalResources() {
		body, ok := db.Documents[collection][id]
		if !ok {
			return documentView{}, fmt.Errorf("%w: document %q", core.ErrNotFound, id)
		}
		return documentView{ID: id, Value: jsontext.Value(body).Clone()}, nil
	}
	client, err := s.deps.Documents(p, db.Name)
	if err != nil {
		return documentView{}, err
	}
	document, err := client.Collection(collection).Get(ctx, id)
	if err != nil {
		return documentView{}, core.ProviderError{Action: "document read", Err: err}
	}
	return documentView{ID: id, Value: jsontext.Value(document.Document)}, nil
}

// WriteDocument creates, replaces, merge-patches, or deletes one document and
// records the new version in its history. Creating without an ID generates
// one.
func (s Service) WriteDocument(ctx context.Context, p core.Principal, databaseID, collection, operation, id string, body []byte) (documentWrite, error) {
	db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return documentWrite{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanWrite); err != nil {
		return documentWrite{}, err
	}
	if !slices.Contains(documentOperations, operation) {
		return documentWrite{}, core.Invalid("operation must be create, replace, patch, or delete")
	}
	id = strings.TrimSpace(id)
	if operation == "create" && id == "" {
		id = core.NewID("doc")
	}
	if id == "" || len(id) > 256 {
		return documentWrite{}, core.Invalid("document IDs use 1-256 characters")
	}
	if operation != "delete" && (len(body) == 0 || len(body) > maxDocumentBytes || !jsontext.Value(body).IsValid()) {
		return documentWrite{}, core.Invalid("document must be valid JSON within 1 MiB")
	}
	result := documentWrite{ID: id, Operation: operation}
	if s.deps.LocalResources() {
		return result, s.updateDatabase(ctx, p, db.ID, core.Organization.CanWrite, func(current *core.Database) error {
			return writeDemoDocument(current, collection, operation, id, body)
		})
	}
	client, err := s.deps.Documents(p, db.Name)
	if err != nil {
		return documentWrite{}, err
	}
	var opts []v1.WriteOption
	if operation != "create" {
		current, readErr := client.Collection(collection).Get(ctx, id)
		if readErr != nil {
			return documentWrite{}, core.ProviderError{Action: "document read", Err: readErr}
		}
		opts = append(opts, v1.IfMatch(current.Version))
	}
	switch operation {
	case "create":
		_, err = client.Collection(collection).Put(ctx, id, stdjson.RawMessage(body), v1.IfNoneMatchAny())
	case "replace":
		_, err = client.Collection(collection).Put(ctx, id, stdjson.RawMessage(body), opts...)
	case "patch":
		_, err = client.Collection(collection).Patch(ctx, id, v1.MergePatch(stdjson.RawMessage(body)), opts...)
	case "delete":
		_, err = client.Collection(collection).Delete(ctx, id, opts...)
	}
	if err != nil {
		return documentWrite{}, core.ProviderError{Action: "document " + operation, Err: err}
	}
	return result, nil
}

func writeDemoDocument(db *core.Database, collection, operation, id string, body []byte) error {
	if db.Documents == nil {
		db.Documents = make(map[string]map[string][]byte)
	}
	docs := db.Documents[collection]
	if docs == nil {
		docs = make(map[string][]byte)
	}
	old, exists := docs[id]
	if operation == "create" && exists {
		return fmt.Errorf("%w: document %q already exists", core.ErrConflict, id)
	}
	if operation != "create" && !exists {
		return fmt.Errorf("%w: document %q", core.ErrNotFound, id)
	}
	switch operation {
	case "create", "replace":
		docs[id] = slices.Clone(body)
	case "patch":
		merged, err := mergePatch(old, body)
		if err != nil {
			return core.Invalid("merge patch must be a JSON object")
		}
		docs[id] = merged
	case "delete":
		delete(docs, id)
	}
	if db.DocumentHistory == nil {
		db.DocumentHistory = make(map[string]map[string][][]byte)
	}
	if db.DocumentHistory[collection] == nil {
		db.DocumentHistory[collection] = make(map[string][][]byte)
	}
	version := docs[id]
	if operation == "delete" {
		version = []byte("null")
	}
	db.DocumentHistory[collection][id] = append(db.DocumentHistory[collection][id], slices.Clone(version))
	db.Documents[collection] = docs
	return nil
}

// History lists a document's recorded versions, oldest first. Demo versions
// carry their bodies; the live data plane reports revisions only.
func (s Service) History(ctx context.Context, p core.Principal, databaseID, collection, id string) ([]historyEntry, error) {
	db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return nil, err
	}
	if s.deps.LocalResources() {
		versions := db.DocumentHistory[collection][id]
		entries := make([]historyEntry, 0, len(versions))
		for i, version := range versions {
			deleted := jsontext.Value(version).Kind() == 'n'
			entry := historyEntry{Revision: strconv.Itoa(i + 1), Deleted: deleted}
			if !deleted {
				entry.Value = jsontext.Value(version).Clone()
			}
			entries = append(entries, entry)
		}
		return entries, nil
	}
	// TODO(TS-463): Documents v1 has no document history endpoint.
	return nil, core.ErrUnavailable
}

// Schema returns a collection's schema as JSON, in the shape SetSchema
// accepts.
func (s Service) Schema(ctx context.Context, p core.Principal, databaseID, collection string) (jsontext.Value, error) {
	db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return nil, err
	}
	if s.deps.LocalResources() {
		body, ok := db.Schemas[collection]
		if !ok {
			return nil, fmt.Errorf("%w: no schema for collection %q", core.ErrNotFound, collection)
		}
		return jsontext.Value(body).Clone(), nil
	}
	client, err := s.deps.DataPlane(p)
	if err != nil {
		return nil, err
	}
	schema, err := client.Databases.GetCollectionSchema(ctx, db.Name, collection)
	if err != nil {
		return nil, core.ProviderError{Action: "schema read", Err: err}
	}
	encoded, err := json.Marshal(schema.Schema)
	return jsontext.Value(encoded), err
}

// SetSchema applies a collection schema whose name matches the collection.
func (s Service) SetSchema(ctx context.Context, p core.Principal, databaseID, collection string, body []byte) error {
	db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return err
	}
	var schema sdk.CollectionSchema
	if len(body) == 0 || len(body) > maxSchemaBytes || json.Unmarshal(body, &schema) != nil || schema.Name != collection || schema.Validate() != nil {
		return core.Invalid("schema must be valid JSON for the selected collection, with name equal to the collection")
	}
	if s.deps.LocalResources() {
		return s.updateDatabase(ctx, p, db.ID, core.Organization.CanManage, func(current *core.Database) error {
			if current.Schemas == nil {
				current.Schemas = make(map[string][]byte)
			}
			current.Schemas[collection] = slices.Clone(body)
			return nil
		})
	}
	client, err := s.deps.DataPlane(p)
	if err != nil {
		return err
	}
	current, err := client.Databases.GetCollectionSchema(ctx, db.Name, collection)
	if err == nil {
		_, err = client.Databases.PutCollectionSchema(ctx, db.Name, collection, schema, sdk.WithIdempotencyKey(core.NewID("schema")), sdk.WithSchemaGeneration(current.Generation))
	}
	if err != nil {
		return core.ProviderError{Action: "schema apply", Err: err}
	}
	return nil
}

// Indexes lists a collection's indexed fields and their build state.
func (s Service) Indexes(ctx context.Context, p core.Principal, databaseID, collection string) ([]indexView, error) {
	db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return nil, err
	}
	if s.deps.LocalResources() {
		indexes := make([]indexView, 0, len(db.Indexes[collection]))
		for _, field := range db.Indexes[collection] {
			state := "ready"
			for _, build := range db.IndexBuilds {
				if build.Collection == collection && build.Field == field {
					state = build.Status
				}
			}
			indexes = append(indexes, indexView{Field: field, State: state})
		}
		return indexes, nil
	}
	client, err := s.deps.DataPlane(p)
	if err != nil {
		return nil, err
	}
	page, err := client.Databases.ListIndexes(ctx, db.Name, collection, sdk.PageRequest{})
	if err != nil {
		return nil, core.ProviderError{Action: "index list", Err: err}
	}
	indexes := make([]indexView, 0, len(page.Indexes))
	for _, index := range page.Indexes {
		indexes = append(indexes, indexView{Field: index.Field, State: index.State, BackfillRemaining: index.BackfillRemaining})
	}
	return indexes, nil
}

// CreateIndex adds a single-field index to a collection.
func (s Service) CreateIndex(ctx context.Context, p core.Principal, databaseID, collection, field string) error {
	db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return err
	}
	field = strings.TrimSpace(field)
	if field == "" || len(field) > 128 {
		return core.Invalid("index fields use 1-128 characters")
	}
	if s.deps.LocalResources() {
		return s.updateDatabase(ctx, p, db.ID, core.Organization.CanManage, func(current *core.Database) error {
			if current.Indexes == nil {
				current.Indexes = make(map[string][]string)
			}
			if !slices.Contains(current.Indexes[collection], field) {
				current.Indexes[collection] = append(current.Indexes[collection], field)
			}
			return nil
		})
	}
	client, err := s.deps.DataPlane(p)
	if err != nil {
		return err
	}
	if _, err := client.Databases.CreateIndex(ctx, db.Name, collection, sdk.CreateIndexRequest{Field: field}, sdk.WithIdempotencyKey(core.NewID("index"))); err != nil {
		return core.ProviderError{Action: "index create", Err: err}
	}
	return nil
}

// Recommendations lists a database's index recommendations, optionally for
// one collection.
func (s Service) Recommendations(ctx context.Context, p core.Principal, databaseID, collection string) ([]recommendationView, error) {
	db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return nil, err
	}
	views := []recommendationView{}
	for _, recommendation := range db.IndexRecommendations {
		if recommendation.DatabaseID == db.ID && (collection == "" || recommendation.Collection == collection) {
			views = append(views, recommendationView{
				ID: recommendation.ID, Collection: recommendation.Collection, Fields: slices.Clone(recommendation.Fields),
				Reason: recommendation.Reason, State: recommendation.State,
			})
		}
	}
	return views, nil
}

// ApplyRecommendation builds a recommendation's indexes. The database and
// collection are optional filters that must match the recommendation.
func (s Service) ApplyRecommendation(ctx context.Context, p core.Principal, databaseID, collection, recommendationID string) (recommendationResult, error) {
	if _, _, err := s.project(ctx, p); err != nil {
		return recommendationResult{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return recommendationResult{}, err
	}
	if (collection != "" && !validCollection(collection)) || recommendationID == "" || len(recommendationID) > 128 {
		return recommendationResult{}, core.Invalid("a recommendation ID and valid collection are required")
	}
	if !s.deps.LocalResources() {
		return recommendationResult{}, errDemoOnly
	}
	var result recommendationResult
	err := s.updateProject(ctx, p, core.Organization.CanManage, func(project *core.Project) error {
		// The callback may run again, so each run starts a fresh result.
		result = recommendationResult{RecommendationID: recommendationID, Status: "applied"}
		var current core.Database
		recommendationIndex, matches := -1, 0
		for id, database := range project.Databases {
			if database.ProjectID != project.ID || (databaseID != "" && id != databaseID) {
				continue
			}
			for i, recommendation := range database.IndexRecommendations {
				if recommendation.ID != recommendationID || recommendation.DatabaseID != id || (collection != "" && recommendation.Collection != collection) {
					continue
				}
				matches++
				result.DatabaseID, result.Collection = id, recommendation.Collection
				current, recommendationIndex = database, i
			}
		}
		if matches == 0 {
			return fmt.Errorf("%w: recommendation %q", core.ErrNotFound, recommendationID)
		}
		if matches > 1 {
			return core.ErrConflict
		}
		if !validCollection(result.Collection) {
			return core.ErrInvalid
		}
		recommendation := current.IndexRecommendations[recommendationIndex]
		if recommendation.State == "applied" {
			result.Status = "already-applied"
			return nil
		}
		if recommendation.State != "recommended" || len(recommendation.Fields) == 0 {
			return fmt.Errorf("%w: recommendation is %s", core.ErrConflict, recommendation.State)
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
			if slices.Contains(current.Indexes[result.Collection], field) {
				continue
			}
			current.Indexes[result.Collection] = append(current.Indexes[result.Collection], field)
			current.IndexBuilds = append(current.IndexBuilds, core.IndexBuild{ID: core.NewID("index"), Collection: result.Collection, Field: field, Status: "building"})
		}
		current.IndexRecommendations[recommendationIndex].State = "applied"
		project.Databases[result.DatabaseID] = current
		return nil
	})
	if err != nil {
		return recommendationResult{}, err
	}
	return result, nil
}

// Migration returns the index backfill progress shown for a collection, or
// nil when none is running.
func (s Service) Migration(ctx context.Context, p core.Principal, databaseID, collection string) (*demoIndexMigration, error) {
	a, project, err := s.project(ctx, p)
	if err != nil {
		return nil, err
	}
	db, ok := project.Databases[databaseID]
	if !ok || db.ProjectID != project.ID {
		return nil, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	if !validCollection(collection) {
		return nil, core.Invalid("collection names use 1-128 characters without slashes or line breaks")
	}
	if !s.deps.LocalResources() {
		return nil, nil
	}
	return marioIndexMigration(a, p, db, collection), nil
}

// CheckSQL analyzes one read-only statement without running it.
func (s Service) CheckSQL(ctx context.Context, p core.Principal, databaseID, statement string) (sqlAnalysis, error) {
	if _, err := s.database(ctx, p, databaseID); err != nil {
		return sqlAnalysis{}, err
	}
	if len(statement) > maxSQLCheckBytes {
		return sqlAnalysis{}, core.Invalid("SQL must be 16 KiB or less")
	}
	return analyzeSQL(statement), nil
}

// sqlRun is a query's result: the JSON API clients and tools receive, and the
// table the console shows.
type sqlRun struct {
	JSON  jsontext.Value
	Table sqlResult
}

// RunSQL runs one read-only statement. The demo supports SELECT * FROM
// collection [LIMIT n].
func (s Service) RunSQL(ctx context.Context, p core.Principal, databaseID, statement string) (sqlRun, error) {
	db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return sqlRun{}, err
	}
	statement = strings.TrimSpace(statement)
	if len(statement) == 0 || len(statement) > maxSQLCheckBytes || sdk.ValidateSQL(statement, nil) != nil {
		return sqlRun{}, core.Invalid("enter one read-only SQL query")
	}
	var result any
	var table sqlResult
	if s.deps.LocalResources() {
		rows, matched, err := demoSelect(db, statement)
		if err != nil {
			return sqlRun{}, err
		}
		result = struct {
			Rows []documentView `json:"rows"`
		}{Rows: rows}
		table = newSQLResult(rows, matched)
	} else {
		// TODO(TS-463): Documents v1 has no SQL execution endpoint.
		return sqlRun{}, core.ErrUnavailable
	}
	encoded, err := json.Marshal(result)
	return sqlRun{JSON: jsontext.Value(encoded), Table: table}, err
}

// demoSelect answers SELECT * FROM collection [LIMIT n] over the demo
// documents in ID order, with the number of documents the query matched. An
// explicit LIMIT is part of the query, so it matches only the rows it keeps;
// without one the demo still returns at most maxDemoRows, and every document
// counts as matched so the result says it was cut.
func demoSelect(db core.Database, statement string) ([]documentView, int, error) {
	matches := selectPattern.FindStringSubmatch(statement)
	if matches == nil {
		return nil, 0, core.Invalid("The in-memory demo runs SELECT * FROM collection [LIMIT n] only. Check SQL validates any other read-only statement.")
	}
	documents := db.Documents[matches[1]]
	rows := make([]documentView, 0, len(documents))
	for id, body := range documents {
		rows = append(rows, documentView{ID: id, Value: slices.Clone(body)})
	}
	slices.SortFunc(rows, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
	if matches[2] == "" {
		return rows[:min(len(rows), maxDemoRows)], len(rows), nil
	}
	limit, err := strconv.Atoi(matches[2])
	if err != nil || limit < 1 || limit > maxDemoRows {
		return nil, 0, core.Invalid("Demo queries return 1 to " + strconv.Itoa(maxDemoRows) + " rows; use a LIMIT in that range.")
	}
	rows = rows[:min(len(rows), limit)]
	return rows, len(rows), nil
}

// SavedQueries lists the project's saved queries by name.
func (s Service) SavedQueries(ctx context.Context, p core.Principal) ([]savedQueryView, error) {
	_, project, err := s.project(ctx, p)
	if err != nil {
		return nil, err
	}
	queries := make([]savedQueryView, 0, len(project.SavedQueries))
	for _, query := range project.SavedQueries {
		queries = append(queries, savedQueryView(query))
	}
	slices.SortFunc(queries, func(a, b savedQueryView) int { return strings.Compare(a.Name, b.Name) })
	return queries, nil
}

// SaveQuery shares a read-only query with everyone in the project.
func (s Service) SaveQuery(ctx context.Context, p core.Principal, name, statement string) (savedQueryView, error) {
	if _, _, err := s.project(ctx, p); err != nil {
		return savedQueryView{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanWrite); err != nil {
		return savedQueryView{}, err
	}
	name, statement = strings.TrimSpace(name), strings.TrimSpace(statement)
	if name == "" || len(name) > 128 {
		return savedQueryView{}, core.Invalid("saved query names use 1-128 characters")
	}
	if statement == "" || len(statement) > maxSQLCheckBytes || sdk.ValidateSQL(statement, nil) != nil {
		return savedQueryView{}, core.Invalid("saved queries hold one read-only SQL query")
	}
	query := core.SavedQuery{ID: core.NewID("qry"), Name: name, SQL: statement, CreatedAt: time.Now().UTC()}
	err := s.updateProject(ctx, p, core.Organization.CanWrite, func(project *core.Project) error {
		if project.SavedQueries == nil {
			project.SavedQueries = make(map[string]core.SavedQuery)
		}
		project.SavedQueries[query.ID] = query
		return nil
	})
	if err != nil {
		return savedQueryView{}, err
	}
	return savedQueryView(query), nil
}

// DeleteSavedQuery removes one saved query from the project.
func (s Service) DeleteSavedQuery(ctx context.Context, p core.Principal, id string) error {
	if _, _, err := s.project(ctx, p); err != nil {
		return err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanWrite); err != nil {
		return err
	}
	return s.updateProject(ctx, p, core.Organization.CanWrite, func(project *core.Project) error {
		if _, ok := project.SavedQueries[id]; !ok {
			return fmt.Errorf("%w: saved query %q", core.ErrNotFound, id)
		}
		delete(project.SavedQueries, id)
		return nil
	})
}
