package data

import (
	"cmp"
	"context"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"errors"
	"maps"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/databases"
	"terrascale.tech/dashboard/internal/features/notfound"
	sdk "terrascale.tech/sdk"
	"terrascale.tech/sdk/v1"
)

// Sections are the database detail tabs this slice renders. History is part
// of the documents tab.
var sections = map[string]struct{ Tab, Title string }{
	"documents": {"documents", "Documents"},
	"history":   {"documents", "Document history"},
	"sql":       {"sql", "SQL console"},
	"schema":    {"schema", "Schema"},
	"indexes":   {"indexes", "Indexes"},
}

// indexViews are the sub-views of the indexes tab.
var indexViews = []struct{ Key, Label string }{
	{"overview", "Overview"},
	{"templates", "Templates"},
	{"recommendations", "Recommendations"},
	{"migration", "Migration"},
}

type pageData struct {
	Section  string
	Database core.Database
	// System marks a system database: read live from TerraBase, secrets
	// masked, never changed here.
	System          bool
	CanWrite        bool
	CanManage       bool
	Demo            bool
	Search          string
	IndexView       string
	Collection      string
	Collections     []collectionEntry // the documents tab's sub-navigation
	Documents       []documentView
	Schema          string
	Indexes         []string
	IndexBuilds     []core.IndexBuild
	Recommendations []core.IndexRecommendation
	Migration       *demoIndexMigration
	Queries         []core.SavedQuery
	DocumentID      string
	EditJSON        string
	// EditOperation keeps the replace or patch choice of a failed edit.
	EditOperation string
	// DeleteID names the document whose delete confirmation is open.
	DeleteID    string
	SQL         string
	SQLResult   *sqlResult
	SQLAnalysis sqlAnalysis
	// CheckRequested is set when the statement was sent to the server check.
	CheckRequested bool
	SQLChecked     bool
	// SQLCatalog and SQLDiagnostics feed the SQL editor's completions and
	// inline problems.
	SQLCatalog sqlCatalog
	// SQLHistory is the console's run history, newest first.
	SQLHistory []string
	// SQLDiagnostics apply while the editor still holds the checked SQL.
	SQLDiagnostics struct {
		SQL         string          `json:"sql"`
		Diagnostics []sqlDiagnostic `json:"diagnostics"`
	}
	History []historyVersion
	// Error reports that the section could not load from the data plane.
	Error string
	// FormError explains why a form post failed; Dialog names the dialog
	// that reopens with the submitted input, if the form came from one.
	FormError string
	Dialog    string
	// NewDocumentID, NewDocument, QueryName, and IndexField refill the
	// add-document, save-query, and create-index forms after a failed post.
	NewDocumentID string
	NewDocument   string
	QueryName     string
	IndexField    string
	// NewCollection refills the new-collection form after a failed post.
	NewCollection string
	// Layout is how the documents tab shows a page: grid, one column per
	// top-level field, or list, one JSON preview per document. Order, Page,
	// PageCount, Matched, and More place the page in the collection; Grid
	// lays it out.
	Layout    string
	Order     documentSort
	Page      int
	PageCount int
	Matched   int
	More      bool
	Grid      sqlResult
}

const (
	// documentsPerPage is the page size of the documents tab.
	documentsPerPage = 25
	// maxSortField bounds the field name a sort link carries.
	maxSortField = 128
)

// collectionEntry is one collection in the documents sub-navigation. Count
// is empty when counting would cost a data plane scan.
type collectionEntry struct {
	Name  string
	Count string
}

type historyVersion struct {
	Number  string
	Body    string
	Deleted bool
}

func mountPages(mux *http.ServeMux, deps core.Deps) {
	for section := range sections {
		mux.HandleFunc("GET /databases/{databaseID}/"+section, func(w http.ResponseWriter, r *http.Request) {
			renderSection(w, r, deps, r.PathValue("databaseID"), section, http.StatusOK, nil)
		})
	}
	mux.HandleFunc("GET /databases/{databaseID}/documents/{collection}", func(w http.ResponseWriter, r *http.Request) {
		renderSection(w, r, deps, r.PathValue("databaseID"), "documents", http.StatusOK, nil)
	})
}

// sectionPath is the URL of a data section of one database.
func sectionPath(databaseID, section string, query url.Values) string {
	return databases.TabPath(databaseID, section, query)
}

// browseKeys are the documents tab's browsing state: layout, sort, search,
// and page.
var browseKeys = []string{"view", "sort", "order", "q", "page"}

// browseState is the browsing state a document form posted back, for the
// redirect that returns to it.
func browseState(r *http.Request) url.Values {
	query := url.Values{}
	for _, key := range browseKeys {
		if value := r.PostFormValue(key); value != "" {
			query.Set(key, value)
		}
	}
	return query
}

// documentsPath is the deep link to one collection's documents.
func documentsPath(databaseID, collection string, query url.Values) string {
	path := "/databases/" + url.PathEscape(databaseID) + "/documents/" + url.PathEscape(collection)
	if len(query) > 0 {
		path += "?" + query.Encode()
	}
	return path
}

// renderSection renders one data section of a database. Form posts pass
// their outcome through edit and a status, so the input they carry and the
// result never travel in the URL.
func renderSection(w http.ResponseWriter, r *http.Request, deps core.Deps, databaseID, section string, status int, edit func(*pageData)) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		core.RedirectToLogin(w, r)
		return
	}
	service := Service{deps: deps}
	ctx := r.Context()
	a, project, err := service.project(ctx, p)
	if err != nil {
		if errors.Is(err, core.ErrUnauthorized) {
			core.RedirectToLogin(w, r)
			return
		}
		writeError(w, err)
		return
	}
	db, ok := project.Databases[databaseID]
	if !ok {
		notfound.Render(w, r)
		return
	}
	// A system database has no history, schema or indexes here: TerraBase
	// reports none of them to the dashboard.
	if section == "history" || !databases.Shows(db, sections[section].Tab) {
		if core.SystemDatabase(db) {
			http.Redirect(w, r, databases.TabPath(db.ID, "documents", nil), http.StatusSeeOther)
			return
		}
	}
	query := r.URL.Query()
	org := a.Organizations[p.OrganizationID]
	system := core.SystemDatabase(db)
	data := pageData{
		Section:   section,
		Database:  db,
		System:    system,
		CanWrite:  org.CanWrite(p.UserID) && !system,
		CanManage: org.CanManage(p.UserID) && !system,
		Demo:      deps.Demo,
		// Document forms post the browsing state back with their change.
		Search:    strings.TrimSpace(r.FormValue("q")),
		IndexView: query.Get("view"),
		// Deep links carry the collection in the path, form posts in their
		// body.
		Collection: strings.TrimSpace(cmp.Or(r.PathValue("collection"), r.FormValue("collection"))),
		DocumentID: query.Get("id"),
	}
	if section == "documents" {
		loadCollections(ctx, service, p, &data)
	}
	if !validCollection(data.Collection) {
		data.Collection = "users"
	}
	if !slices.ContainsFunc(indexViews, func(v struct{ Key, Label string }) bool { return v.Key == data.IndexView }) {
		data.IndexView = "overview"
	}
	if len(data.Search) > 256 {
		data.Search = data.Search[:256]
	}
	if section == "documents" {
		data.DocumentID = query.Get("edit")
		data.DeleteID = query.Get("delete")
		data.Layout = "grid"
		if r.FormValue("view") == "list" {
			data.Layout = "list"
		}
		if field := r.FormValue("sort"); len(field) <= maxSortField {
			data.Order = documentSort{Field: field, Desc: r.FormValue("order") == "desc"}
		}
		data.Page, _ = strconv.Atoi(r.FormValue("page"))
	}
	switch {
	case system:
		if section == "documents" {
			loadLive(ctx, service, p, &data)
		}
	case deps.LocalResources():
		loadDemo(&data, a, p, db)
	default:
		loadLive(ctx, service, p, &data)
	}
	data.Queries = slices.SortedFunc(maps.Values(project.SavedQueries), func(a, b core.SavedQuery) int { return strings.Compare(a.Name, b.Name) })
	if section == "sql" {
		data.SQL = "SELECT * FROM " + data.Collection + " LIMIT 20"
		if saved, ok := project.SavedQueries[query.Get("savedQueryId")]; ok {
			data.SQL = saved.SQL
		}
	}
	if section == "documents" && data.DocumentID != "" {
		if i := slices.IndexFunc(data.Documents, func(d documentView) bool { return d.ID == data.DocumentID }); i >= 0 {
			data.EditJSON = indented(data.Documents[i].Value)
		}
	}
	if section == "documents" && !slices.ContainsFunc(data.Documents, func(d documentView) bool { return d.ID == data.DeleteID }) {
		data.DeleteID = ""
	}
	if search := strings.ToLower(data.Search); search != "" && section == "documents" {
		data.Documents = slices.DeleteFunc(data.Documents, func(document documentView) bool {
			return !strings.Contains(strings.ToLower(document.ID+" "+document.Value.String()), search)
		})
	}
	if section == "documents" {
		paginate(&data)
	}
	if section == "sql" {
		data.SQLHistory = sqlHistory(r.PostFormValue("history"))
	}
	if edit != nil {
		edit(&data)
	}
	if section == "sql" {
		data.SQLAnalysis = analyzeSQL(data.SQL)
		data.SQLChecked = data.CheckRequested && data.SQLAnalysis.OK
		data.SQLDiagnostics.SQL, data.SQLDiagnostics.Diagnostics = data.SQL, sqlDiagnostics(data.SQL)
		// The editor still completes keywords when collections cannot load.
		data.SQLCatalog, _ = service.SQLCatalog(ctx, p, db.ID)
	}
	if section == "schema" && data.Schema == "" {
		data.Schema = starterSchema(data.Collection)
	}
	frame := databases.FrameProps{Database: db, Tab: sections[section].Tab, Collection: data.Collection}
	page := core.Page{Layout: "shell", Title: db.Name + " · " + sections[section].Title, Principal: p, View: func(page core.Page) templ.Component { return sectionPage(page, frame, data) }, CSRFToken: deps.CSRF.Token(w, r), Status: status}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render database data", http.StatusInternalServerError)
	}
}

// formFailed answers a form post that could not be applied. API clients get
// the status and message as plain text; a browser gets the section again with
// the message, and edit refills the input it submitted.
func formFailed(w http.ResponseWriter, r *http.Request, deps core.Deps, databaseID, section string, status int, message string, edit func(*pageData)) {
	if !core.WantsHTML(r) || databaseID == "" {
		http.Error(w, message, status)
		return
	}
	renderSection(w, r, deps, databaseID, section, status, func(d *pageData) {
		d.FormError = message
		if edit != nil {
			edit(d)
		}
	})
}

// starterSchema is the smallest valid schema of a collection, the starting
// point when none is declared yet.
func starterSchema(collection string) string {
	encoded, err := json.Marshal(sdk.NewCollectionSchema(collection))
	if err != nil {
		return ""
	}
	return indented(encoded)
}

// indented formats a document with two-space indentation for reading and
// editing; values that cannot be formatted are shown as stored.
func indented(value jsontext.Value) string {
	formatted := value.Clone()
	if err := formatted.Indent(jsontext.WithIndent("  ")); err != nil {
		return value.String()
	}
	return formatted.String()
}

// paginate cuts the sorted, filtered documents to the open page and lays
// the page out for the grid.
func paginate(data *pageData) {
	data.Matched = len(data.Documents)
	data.PageCount = max(1, (data.Matched+documentsPerPage-1)/documentsPerPage)
	data.Page = min(max(data.Page, 1), data.PageCount)
	start := (data.Page - 1) * documentsPerPage
	data.Documents = data.Documents[start:min(start+documentsPerPage, data.Matched)]
	if data.Layout == "grid" {
		data.Grid = newSQLResult(data.Documents, len(data.Documents))
	}
}

// loadCollections lists the database's collections for the sub-navigation
// and settles which one is open: without a choice, users or else the first
// one; a chosen collection that holds nothing yet is listed too. Demo and
// system collections carry their document counts.
func loadCollections(ctx context.Context, service Service, p core.Principal, data *pageData) {
	counts := make(map[string]int64)
	var names []string
	var err error
	if data.System {
		var stats []v1.CollectionStats
		stats, err = service.deps.SystemCollections(ctx, p, data.Database)
		for _, collection := range stats {
			names = append(names, collection.Name)
			counts[collection.Name] = collection.Documents
		}
	} else {
		names, err = service.Collections(ctx, p, data.Database.ID)
		if service.deps.LocalResources() {
			for _, name := range names {
				counts[name] = int64(len(data.Database.Documents[name]))
			}
		}
	}
	if err != nil {
		data.Error = loadError(data, err)
		return
	}
	switch {
	case data.Collection == "" && len(names) > 0 && !slices.Contains(names, "users"):
		data.Collection = names[0]
	case validCollection(data.Collection) && !slices.Contains(names, data.Collection):
		names = append(names, data.Collection)
		slices.Sort(names)
	}
	for _, name := range names {
		entry := collectionEntry{Name: name}
		if count, ok := counts[name]; ok {
			entry.Count = strconv.FormatInt(count, 10)
		}
		data.Collections = append(data.Collections, entry)
	}
}

// loadDemo reads the selected collection from the in-memory demo database.
func loadDemo(data *pageData, a core.Account, p core.Principal, db core.Database) {
	if data.Section == "indexes" && data.IndexView == "migration" {
		data.Migration = marioIndexMigration(a, p, db, data.Collection)
	}
	for id, body := range db.Documents[data.Collection] {
		data.Documents = append(data.Documents, documentView{ID: id, Value: body})
	}
	sortDocuments(data.Documents, data.Order)
	if schema, ok := db.Schemas[data.Collection]; ok {
		data.Schema = indented(schema)
	}
	data.Indexes = slices.Clone(db.Indexes[data.Collection])
	for _, build := range db.IndexBuilds {
		if build.Collection == data.Collection {
			data.IndexBuilds = append(data.IndexBuilds, build)
		}
	}
	for _, recommendation := range db.IndexRecommendations {
		if recommendation.DatabaseID == db.ID && recommendation.Collection == data.Collection {
			data.Recommendations = append(data.Recommendations, recommendation)
		}
	}
	if data.Section == "history" && data.DocumentID != "" {
		for i, version := range db.DocumentHistory[data.Collection][data.DocumentID] {
			data.History = append(data.History, historyVersion{Number: strconv.Itoa(i + 1), Body: indented(version), Deleted: jsontext.Value(version).Kind() == 'n'})
		}
	}
}

// loadLive reads what the open section needs through the data plane SDK.
func loadLive(ctx context.Context, service Service, p core.Principal, data *pageData) {
	var err error
	switch data.Section {
	case "documents":
		data.Documents, data.More, err = service.Browse(ctx, p, data.Database.ID, data.Collection, data.Order)
	case "history":
		if data.DocumentID == "" {
			return
		}
		var entries []historyEntry
		entries, err = service.History(ctx, p, data.Database.ID, data.Collection, data.DocumentID)
		for _, entry := range entries {
			data.History = append(data.History, historyVersion{Number: entry.Revision, Deleted: entry.Deleted})
		}
	case "indexes":
		var indexes []indexView
		indexes, err = service.Indexes(ctx, p, data.Database.ID, data.Collection)
		for _, index := range indexes {
			data.Indexes = append(data.Indexes, index.Field)
		}
	case "schema":
		var schema []byte
		schema, err = service.Schema(ctx, p, data.Database.ID, data.Collection)
		data.Schema = indented(schema)
	}
	if err != nil {
		data.Error = loadError(data, err)
	}
}

// loadError is what the page says about a failed read; a system database's
// provider details go to the log only.
func loadError(data *pageData, err error) string {
	if data.System {
		return core.SystemUnavailable(err)
	}
	return err.Error()
}
