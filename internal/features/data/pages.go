package data

import (
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
	Section         string
	Database        core.Database
	CanWrite        bool
	CanManage       bool
	Demo            bool
	Search          string
	IndexView       string
	Collection      string
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
	History        []historyVersion
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
}

// sectionPath is the URL of a data section of one database.
func sectionPath(databaseID, section string, query url.Values) string {
	return databases.TabPath(databaseID, section, query)
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
	query := r.URL.Query()
	org := a.Organizations[p.OrganizationID]
	data := pageData{
		Section:   section,
		Database:  db,
		CanWrite:  org.CanWrite(p.UserID),
		CanManage: org.CanManage(p.UserID),
		Demo:      deps.Demo,
		Search:    strings.TrimSpace(query.Get("q")),
		IndexView: query.Get("view"),
		// Form posts carry the collection in their body.
		Collection: strings.TrimSpace(r.FormValue("collection")),
		DocumentID: query.Get("id"),
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
	}
	if deps.LocalResources() {
		loadDemo(&data, a, p, db)
	} else {
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
	if edit != nil {
		edit(&data)
	}
	if section == "sql" {
		data.SQLAnalysis = analyzeSQL(data.SQL)
		data.SQLChecked = data.CheckRequested && data.SQLAnalysis.OK
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

// loadDemo reads the selected collection from the in-memory demo database.
func loadDemo(data *pageData, a core.Account, p core.Principal, db core.Database) {
	if data.Section == "indexes" && data.IndexView == "migration" {
		data.Migration = marioIndexMigration(a, p, db, data.Collection)
	}
	for id, body := range db.Documents[data.Collection] {
		data.Documents = append(data.Documents, documentView{ID: id, Value: body})
	}
	slices.SortFunc(data.Documents, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
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
		data.Documents, err = service.Documents(ctx, p, data.Database.ID, documentQuery{Collection: data.Collection})
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
		data.Error = err.Error()
	}
}
