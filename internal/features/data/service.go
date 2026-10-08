package data

import (
	"bytes"
	"cmp"
	"context"
	stdjson "encoding/json"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"errors"
	"fmt"
	"maps"
	"math/big"
	"regexp"
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
	// maxBrowseDocuments bounds what the documents tab reads per view.
	// ponytail: one query of the first 1000 in sort order; page through
	// cursors if collections outgrow it.
	maxBrowseDocuments = 1000
)

var errDemoOnly = errors.New("index recommendations are only available in the in-memory demo")

// errNoSchemas refuses collection schemas outside the demo: TerraBase does
// not enforce them, so the dashboard keeps none.
var errNoSchemas = errors.New("TerraBase does not take collection schemas yet")

// systemCollection is a TerraBase collection name, quoted in SQL;
// systemField is a top-level field TerraBase SQL can sort by.
var (
	systemCollection = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)
	systemField      = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]{0,127}$`)
)

// systemRows reads a system database's collection through TerraBase SQL,
// SELECT * FROM collection followed by clauses, with secrets masked. more
// reports rows the statement's limit cut.
func (s Service) systemRows(ctx context.Context, p core.Principal, db core.Database, collection, clauses string) (docs []documentView, more bool, err error) {
	if !systemCollection.MatchString(collection) {
		return nil, false, core.Invalid("TerraBase collection names use 1-63 lowercase letters, digits, hyphens, and underscores")
	}
	management, err := s.deps.System(ctx, p)
	if err != nil {
		return nil, false, err
	}
	result, err := management.SQL(ctx, p.OrganizationID, db.ProjectID, db.ID, `SELECT * FROM "`+collection+`" `+clauses)
	if err != nil {
		return nil, false, systemSQLError(err)
	}
	docs, err = systemDocuments(result.Rows)
	return docs, result.Truncated, err
}

// managed returns the database's home region's TerraBase Management acting
// as the signed-in user, and the TerraBase IDs of its project and itself.
func (s Service) managed(ctx context.Context, p core.Principal, project core.Project, db core.Database) (m *v1.ManagementUser, org, proj, id string, err error) {
	m, org, proj, db, err = s.deps.ManageDatabase(ctx, p, project, db)
	return m, org, proj, db.ID, err
}

// terraBaseSQL runs a read-only statement on a database through TerraBase
// Management, as the signed-in user.
func (s Service) terraBaseSQL(ctx context.Context, p core.Principal, project core.Project, db core.Database, statement string) (rows []documentView, matched int, more bool, err error) {
	management, org, proj, id, err := s.managed(ctx, p, project, db)
	if err != nil {
		return nil, 0, false, err
	}
	result, err := management.SQL(ctx, org, proj, id, statement)
	if err != nil {
		return nil, 0, false, systemSQLError(err)
	}
	for _, row := range result.Rows {
		rows = append(rows, documentView{ID: row.ID, Value: jsontext.Value(row.Value)})
	}
	return rows, result.Matched, result.Truncated, nil
}

func systemSQLError(err error) error {
	if apiErr, ok := errors.AsType[*v1.APIError](err); ok && apiErr.Code == v1.ErrInvalidRequest {
		return core.Invalid(apiErr.Message)
	}
	return core.ProviderError{Action: "system document query", Err: err}
}

// systemDocuments masks the secrets of SQL rows. A row that cannot be
// masked fails the read rather than show unmasked.
func systemDocuments(rows []v1.SQLRow) ([]documentView, error) {
	docs := make([]documentView, 0, len(rows))
	for _, row := range rows {
		decoder := stdjson.NewDecoder(bytes.NewReader(row.Value))
		decoder.UseNumber()
		var value any
		if err := decoder.Decode(&value); err != nil {
			return nil, core.ProviderError{Action: "system document decode", Err: err}
		}
		masked, err := stdjson.Marshal(core.MaskSecrets(value))
		if err != nil {
			return nil, core.ProviderError{Action: "system document mask", Err: err}
		}
		docs = append(docs, documentView{ID: row.ID, Value: jsontext.Value(masked)})
	}
	return docs, nil
}

// sqlString quotes a SQL string literal.
func sqlString(value string) string {
	return "'" + strings.ReplaceAll(value, "'", "''") + "'"
}

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

func (s Service) database(ctx context.Context, p core.Principal, databaseID string) (core.Project, core.Database, error) {
	_, project, err := s.project(ctx, p)
	if err != nil {
		return core.Project{}, core.Database{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok || db.ProjectID != project.ID {
		return core.Project{}, core.Database{}, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	return project, db, nil
}

// collectionDatabase resolves the database and validates the collection name.
func (s Service) collectionDatabase(ctx context.Context, p core.Principal, databaseID, collection string) (core.Project, core.Database, error) {
	project, db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return core.Project{}, core.Database{}, err
	}
	if !validCollection(collection) {
		return core.Project{}, core.Database{}, core.Invalid("collection names use 1-128 characters without slashes or line breaks")
	}
	return project, db, nil
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
		if core.SystemDatabase(current) {
			return core.ErrSystemReadOnly
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
	project, db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return nil, err
	}
	if core.SystemDatabase(db) {
		stats, err := s.deps.SystemCollections(ctx, p, db)
		names := make([]string, 0, len(stats))
		for _, collection := range stats {
			names = append(names, collection.Name)
		}
		return names, err
	}
	if s.deps.LocalResources() {
		names := slices.Concat(slices.Collect(maps.Keys(db.Documents)), slices.Collect(maps.Keys(db.Schemas)), slices.Collect(maps.Keys(db.Indexes)))
		slices.Sort(names)
		return slices.Compact(names), nil
	}
	client, err := s.deps.Data(ctx, p, project, db)
	if err != nil {
		return nil, err
	}
	var names []string
	for cursor := ""; ; {
		page, err := client.Collections(ctx, v1.ListOptions{Limit: 200, Cursor: cursor})
		if err != nil {
			return nil, core.ProviderError{Action: "collection list", Err: err}
		}
		for _, item := range page.Items {
			names = append(names, item.Name)
		}
		if cursor = page.NextCursor; cursor == "" {
			return names, nil
		}
	}
}

// CreateCollection creates an empty collection; an existing name conflicts.
func (s Service) CreateCollection(ctx context.Context, p core.Principal, databaseID, name string) error {
	if !collectionName.MatchString(name) {
		return core.Invalid("collection names start with a lowercase letter and use up to 63 lowercase letters, digits, hyphens, and underscores")
	}
	project, db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanWrite); err != nil {
		return err
	}
	if s.deps.LocalResources() {
		return s.updateDatabase(ctx, p, db.ID, core.Organization.CanWrite, func(current *core.Database) error {
			_, documents := current.Documents[name]
			_, schema := current.Schemas[name]
			_, indexes := current.Indexes[name]
			if documents || schema || indexes {
				return fmt.Errorf("%w: collection %q already exists", core.ErrConflict, name)
			}
			if current.Documents == nil {
				current.Documents = make(map[string]map[string][]byte)
			}
			current.Documents[name] = make(map[string][]byte)
			return nil
		})
	}
	client, err := s.deps.Data(ctx, p, project, db)
	if err != nil {
		return err
	}
	if _, err := client.CreateCollection(ctx, name); err != nil {
		if errors.Is(err, v1.ErrNameTaken) {
			return fmt.Errorf("%w: collection %q already exists", core.ErrConflict, name)
		}
		return core.ProviderError{Action: "collection create", Err: err}
	}
	return nil
}

// documentSort orders a collection by a top-level field, or by ID when Field
// is empty.
type documentSort struct {
	Field string
	Desc  bool
}

// Browse reads the first maxBrowseDocuments of a collection in sort order;
// more reports that the collection holds others.
func (s Service) Browse(ctx context.Context, p core.Principal, databaseID, collection string, order documentSort) ([]documentView, bool, error) {
	project, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return nil, false, err
	}
	if core.SystemDatabase(db) {
		// A sort by a secret member would reveal its order.
		key := "_id"
		if systemField.MatchString(order.Field) && !core.SecretField(order.Field) {
			key = order.Field
		}
		if order.Desc {
			key += " DESC"
		}
		return s.systemRows(ctx, p, db, collection, fmt.Sprintf("ORDER BY %s LIMIT %d", key, maxBrowseDocuments))
	}
	var docs []documentView
	if s.deps.LocalResources() {
		for id, body := range db.Documents[collection] {
			docs = append(docs, documentView{ID: id, Value: jsontext.Value(body).Clone()})
		}
		sortDocuments(docs, order)
		return docs, false, nil
	}
	client, err := s.deps.Data(ctx, p, project, db)
	if err != nil {
		return nil, false, err
	}
	path := v1.IDPath
	if order.Field != "" {
		path = "/" + strings.NewReplacer("~", "~0", "/", "~1").Replace(order.Field)
	}
	key := v1.Asc(path)
	if order.Desc {
		key = v1.Desc(path)
	}
	// Bounded scans may return short or empty pages with a cursor, so read
	// on until the cap or the end.
	query := v1.Query{Sort: []v1.SortKey{key}}
	for {
		query.Limit = maxBrowseDocuments - len(docs)
		result, err := client.Collection(collection).Query(ctx, query)
		if err != nil {
			return nil, false, core.ProviderError{Action: "document query", Err: err}
		}
		for _, row := range result.Items {
			docs = append(docs, documentView{ID: row.ID, Value: jsontext.Value(row.Document)})
		}
		if result.NextCursor == "" || len(docs) >= maxBrowseDocuments {
			return docs, result.NextCursor != "", nil
		}
		query.Cursor = result.NextCursor
	}
}

// sortDocuments orders documents by a top-level field, then by ID. Documents
// without the field come first ascending; values of different JSON kinds
// order null, bool, number, string, object, array. Each document's key is
// read once, so sorting never reparses whole documents.
func sortDocuments(docs []documentView, order documentSort) {
	keys := make([]sortKey, len(docs))
	for i, doc := range docs {
		keys[i] = sortKey{doc: doc}
		if order.Field != "" {
			keys[i] = newSortKey(doc, fieldValue(doc.Value, order.Field))
		}
	}
	slices.SortFunc(keys, func(a, b sortKey) int {
		c := a.compare(b)
		if c == 0 {
			c = strings.Compare(a.doc.ID, b.doc.ID)
		}
		if order.Desc {
			return -c
		}
		return c
	})
	for i, key := range keys {
		docs[i] = key.doc
	}
}

// sortKey is a document with its sort value decoded: numbers as exact
// decimals, strings unquoted, anything else as its JSON text.
type sortKey struct {
	doc    documentView
	rank   int
	number *decimal
	text   string
}

func newSortKey(doc documentView, value jsontext.Value) sortKey {
	key := sortKey{doc: doc, rank: kindRank(value), text: string(value)}
	switch value.Kind() {
	case '0':
		key.number = parseDecimal(string(value))
	case '"':
		_ = json.Unmarshal(value, &key.text)
	}
	return key
}

func (a sortKey) compare(b sortKey) int {
	if c := cmp.Compare(a.rank, b.rank); c != 0 {
		return c
	}
	if a.number != nil && b.number != nil {
		return a.number.compare(*b.number)
	}
	return strings.Compare(a.text, b.text)
}

// decimal is a JSON number held exactly, whatever its size: its sign, its
// significant digits without leading or trailing zeros, and the power of ten
// of the first digit. Zero has no digits.
type decimal struct {
	negative bool
	digits   string
	exponent *big.Int
}

// parseDecimal reads a valid JSON number, or returns nil.
func parseDecimal(number string) *decimal {
	negative := strings.HasPrefix(number, "-")
	mantissa, power, _ := strings.Cut(strings.TrimPrefix(number, "-"), "e")
	if power == "" {
		mantissa, power, _ = strings.Cut(mantissa, "E")
	}
	whole, fraction, _ := strings.Cut(mantissa, ".")
	exponent := new(big.Int)
	if power != "" {
		if _, ok := exponent.SetString(power, 10); !ok {
			return nil
		}
	}
	all := whole + fraction
	significant := strings.TrimLeft(all, "0")
	leading := len(all) - len(significant)
	d := &decimal{negative: negative, digits: strings.TrimRight(significant, "0"), exponent: exponent}
	exponent.Add(exponent, big.NewInt(int64(len(whole)-1-leading)))
	return d
}

func (d decimal) sign() int {
	switch {
	case d.digits == "":
		return 0
	case d.negative:
		return -1
	default:
		return 1
	}
}

func (a decimal) compare(b decimal) int {
	sign := a.sign()
	if c := cmp.Compare(sign, b.sign()); c != 0 || sign == 0 {
		return c
	}
	// With equal leading powers, digit strings that start with a non-zero
	// digit and end without zeros order like the fractions they spell.
	c := a.exponent.Cmp(b.exponent)
	if c == 0 {
		c = strings.Compare(a.digits, b.digits)
	}
	return sign * c
}

// fieldValue is a document's top-level field, or nil when it has none. Other
// members are skipped, not copied.
func fieldValue(document jsontext.Value, field string) jsontext.Value {
	if document.Kind() != '{' {
		return nil
	}
	decoder := jsontext.NewDecoder(bytes.NewReader(document))
	if _, err := decoder.ReadToken(); err != nil {
		return nil
	}
	for decoder.PeekKind() == '"' {
		name, err := decoder.ReadToken()
		if err != nil {
			return nil
		}
		if name.String() == field {
			value, err := decoder.ReadValue()
			if err != nil {
				return nil
			}
			return value.Clone()
		}
		if decoder.SkipValue() != nil {
			return nil
		}
	}
	return nil
}

// kindRank orders JSON kinds for sorting; a missing value ranks first.
func kindRank(v jsontext.Value) int {
	if len(v) == 0 {
		return 0
	}
	switch v.Kind() {
	case 'n':
		return 1
	case 't', 'f':
		return 2
	case '0':
		return 3
	case '"':
		return 4
	case '{':
		return 5
	default:
		return 6
	}
}

// Documents lists one page of a collection, ordered by ID and filtered by a
// case-insensitive search over IDs and bodies.
func (s Service) Documents(ctx context.Context, p core.Principal, databaseID string, query documentQuery) ([]documentView, error) {
	project, db, err := s.collectionDatabase(ctx, p, databaseID, query.Collection)
	if err != nil {
		return nil, err
	}
	if query.Limit < 0 || query.Limit > maxListLimit {
		return nil, core.Invalid("limit must be 1 to 100")
	}
	var docs []documentView
	if core.SystemDatabase(db) {
		where := ""
		if query.After != "" {
			where = "WHERE _id > " + sqlString(query.After) + " "
		}
		if docs, _, err = s.systemRows(ctx, p, db, query.Collection, fmt.Sprintf("%sORDER BY _id LIMIT %d", where, cmp.Or(query.Limit, maxListLimit))); err != nil {
			return nil, err
		}
	} else if s.deps.LocalResources() {
		for id, body := range db.Documents[query.Collection] {
			if query.After == "" || id > query.After {
				docs = append(docs, documentView{ID: id, Value: jsontext.Value(body).Clone()})
			}
		}
		slices.SortFunc(docs, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
	} else {
		client, err := s.deps.Data(ctx, p, project, db)
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
	project, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return documentView{}, err
	}
	if core.SystemDatabase(db) {
		docs, _, err := s.systemRows(ctx, p, db, collection, "WHERE _id = "+sqlString(id)+" LIMIT 1")
		if err != nil {
			return documentView{}, err
		}
		if len(docs) == 0 {
			return documentView{}, fmt.Errorf("%w: document %q", core.ErrNotFound, id)
		}
		return docs[0], nil
	}
	if s.deps.LocalResources() {
		body, ok := db.Documents[collection][id]
		if !ok {
			return documentView{}, fmt.Errorf("%w: document %q", core.ErrNotFound, id)
		}
		return documentView{ID: id, Value: jsontext.Value(body).Clone()}, nil
	}
	client, err := s.deps.Data(ctx, p, project, db)
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
	project, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
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
	// TerraBase names a new document itself.
	if operation == "create" && id == "" && s.deps.LocalResources() {
		id = core.NewID()
	}
	if id == "" && operation != "create" {
		return documentWrite{}, core.Invalid("document ID is required")
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
	client, err := s.deps.Data(ctx, p, project, db)
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
		var created *v1.WriteResult
		if id == "" {
			if created, err = client.Collection(collection).Create(ctx, stdjson.RawMessage(body)); err == nil {
				result.ID = created.ID
			}
		} else {
			_, err = client.Collection(collection).Put(ctx, id, stdjson.RawMessage(body), v1.IfNoneMatchAny())
		}
	case "replace":
		_, err = client.Collection(collection).Put(ctx, id, stdjson.RawMessage(body), opts...)
	case "patch":
		_, err = client.Collection(collection).Patch(ctx, id, v1.MergePatch(stdjson.RawMessage(body)), opts...)
	case "delete":
		_, err = client.Collection(collection).Delete(ctx, id, opts...)
	}
	if apiErr, ok := errors.AsType[*v1.APIError](err); ok && apiErr.Code == v1.ErrInvalidRequest {
		// Such as an ID TerraBase does not take.
		return documentWrite{}, core.Invalid(apiErr.Message)
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
	_, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
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
	_, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return nil, err
	}
	if !s.deps.LocalResources() {
		return nil, errNoSchemas
	}
	body, ok := db.Schemas[collection]
	if !ok {
		return nil, fmt.Errorf("%w: no schema for collection %q", core.ErrNotFound, collection)
	}
	return jsontext.Value(body).Clone(), nil
}

// SetSchema applies a collection schema whose name matches the collection.
func (s Service) SetSchema(ctx context.Context, p core.Principal, databaseID, collection string, body []byte) error {
	_, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return err
	}
	if !s.deps.LocalResources() {
		return errNoSchemas
	}
	var schema sdk.CollectionSchema
	if len(body) == 0 || len(body) > maxSchemaBytes || json.Unmarshal(body, &schema) != nil || schema.Name != collection || schema.Validate() != nil {
		return core.Invalid("schema must be valid JSON for the selected collection, with name equal to the collection")
	}
	return s.updateDatabase(ctx, p, db.ID, core.Organization.CanManage, func(current *core.Database) error {
		if current.Schemas == nil {
			current.Schemas = make(map[string][]byte)
		}
		current.Schemas[collection] = slices.Clone(body)
		return nil
	})
}

// Indexes lists a collection's indexed fields and their build state.
func (s Service) Indexes(ctx context.Context, p core.Principal, databaseID, collection string) ([]indexView, error) {
	project, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
	if err != nil {
		return nil, err
	}
	if !s.deps.LocalResources() {
		management, org, proj, id, err := s.managed(ctx, p, project, db)
		if err != nil {
			return nil, err
		}
		list, err := management.Indexes(ctx, org, proj, id)
		if err != nil {
			return nil, core.ProviderError{Action: "index list", Err: err}
		}
		var indexes []indexView
		for _, index := range list {
			if index.Collection == collection {
				indexes = append(indexes, indexView{Field: strings.Join(index.Fields, ", "), State: index.State})
			}
		}
		return indexes, nil
	}
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

// CreateIndex adds a single-field index to a collection.
func (s Service) CreateIndex(ctx context.Context, p core.Principal, databaseID, collection, field string) error {
	project, db, err := s.collectionDatabase(ctx, p, databaseID, collection)
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
	if !s.deps.LocalResources() {
		management, org, proj, id, err := s.managed(ctx, p, project, db)
		if err != nil {
			return err
		}
		_, err = management.CreateIndex(ctx, org, proj, id, v1.IndexRequest{Collection: collection, Fields: []string{field}})
		if apiErr, ok := errors.AsType[*v1.APIError](err); ok && apiErr.Code == v1.ErrInvalidRequest {
			return core.Invalid(apiErr.Message)
		}
		if err != nil && !errors.Is(err, v1.ErrIndexExists) {
			return core.ProviderError{Action: "index create", Err: err}
		}
		return nil
	}
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

// Recommendations lists a database's index recommendations, optionally for
// one collection.
func (s Service) Recommendations(ctx context.Context, p core.Principal, databaseID, collection string) ([]recommendationView, error) {
	_, db, err := s.database(ctx, p, databaseID)
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
			current.IndexBuilds = append(current.IndexBuilds, core.IndexBuild{ID: core.NewID(), Collection: result.Collection, Field: field, Status: "building"})
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
	if _, _, err := s.database(ctx, p, databaseID); err != nil {
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
	project, db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return sqlRun{}, err
	}
	statement = strings.TrimSpace(statement)
	if len(statement) == 0 || len(statement) > maxSQLCheckBytes || sdk.ValidateSQL(statement, nil) != nil {
		return sqlRun{}, core.Invalid("enter one read-only SQL query")
	}
	var result any
	var table sqlResult
	if core.SystemDatabase(db) {
		// Only whole documents leave a system database, so masking sees
		// every member under its own name: a projection could rename a
		// secret, and a filter or sort on one would reveal it.
		matches := selectPattern.FindStringSubmatch(statement)
		if matches == nil {
			return sqlRun{}, core.Invalid("System databases run SELECT * FROM collection [LIMIT n] only, so secrets stay masked.")
		}
		limit := cmp.Or(matches[2], strconv.Itoa(maxDemoRows))
		if n, err := strconv.Atoi(limit); err != nil || n < 1 || n > maxBrowseDocuments {
			return sqlRun{}, core.Invalid("System queries return 1 to " + strconv.Itoa(maxBrowseDocuments) + " rows; use a LIMIT in that range.")
		}
		rows, more, err := s.systemRows(ctx, p, db, matches[1], "ORDER BY _id LIMIT "+limit)
		if err != nil {
			return sqlRun{}, err
		}
		result = struct {
			Rows []documentView `json:"rows"`
		}{Rows: rows}
		// TerraBase reports that rows were cut, not how many matched.
		table = newSQLResult(rows, len(rows))
		table.More = more
	} else if s.deps.LocalResources() {
		rows, matched, err := demoSelect(db, statement)
		if err != nil {
			return sqlRun{}, err
		}
		result = struct {
			Rows []documentView `json:"rows"`
		}{Rows: rows}
		table = newSQLResult(rows, matched)
	} else {
		rows, matched, more, err := s.terraBaseSQL(ctx, p, project, db, statement)
		if err != nil {
			return sqlRun{}, err
		}
		result = struct {
			Rows []documentView `json:"rows"`
		}{Rows: rows}
		table = newSQLResult(rows, matched)
		table.More = more
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
	query := core.SavedQuery{ID: core.NewID(), Name: name, SQL: statement, CreatedAt: time.Now().UTC()}
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
