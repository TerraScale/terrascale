package v1_test

import (
	"math"
	"net/http"
	"strings"
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

func TestCollectionTTLContracts(t *testing.T) {
	const path = parityDatabase + "/collections/users/ttl"
	t.Run("set", func(t *testing.T) {
		col := parityClient(t, http.MethodPut, path, "", `{"path":"/expiresAt"}`, `{"path":"/expiresAt"}`, http.StatusOK).Database(parityProject, "main").Collection("users")
		if err := col.SetTTL(t.Context(), "/expiresAt"); err != nil {
			t.Fatal(err)
		}
	})
	for reply, want := range map[string]string{`{"path":"/expiresAt"}`: "/expiresAt", `{"path":null}`: ""} {
		t.Run("get "+want, func(t *testing.T) {
			col := parityClient(t, http.MethodGet, path, "", "", reply, http.StatusOK).Database(parityProject, "main").Collection("users")
			if got, err := col.TTL(t.Context()); err != nil || got != want {
				t.Fatalf("TTL = %q, %v; want %q", got, err, want)
			}
		})
	}
	t.Run("delete", func(t *testing.T) {
		col := parityClient(t, http.MethodDelete, path, "", "", `{"path":null}`, http.StatusOK).Database(parityProject, "main").Collection("users")
		if err := col.DeleteTTL(t.Context()); err != nil {
			t.Fatal(err)
		}
	})
	t.Run("reject", func(t *testing.T) {
		c, err := tb.New(tb.Config{BaseURL: "http://127.0.0.1:1", Token: "header.project.signature"})
		if err != nil {
			t.Fatal(err)
		}
		if err := c.Database(parityProject, "main").Collection("users").SetTTL(t.Context(), "expiresAt"); err == nil {
			t.Fatal("non-pointer TTL path accepted")
		}
	})
}

func TestNearQuery(t *testing.T) {
	const body = `{"filter":{"eq":["/kind","doc"]},"sort":[{"near":{"path":"/embedding","vector":[0.1,0.2],"metric":"l2"}}],"limit":5}`
	const reply = `{"items":[{"id":"` + parityID + `","version":"opaque","state":"value","document":{},"distance":0.25},{"id":"` + parityID + `","version":"opaque","state":"value","document":{}}]}`
	col := parityClient(t, http.MethodPost, parityDatabase+"/collections/users/query", "", body, reply, http.StatusOK).Database(parityProject, "main").Collection("users")
	res, err := col.Query(t.Context(), tb.Query{Filter: tb.Eq("/kind", "doc"), Sort: []tb.SortKey{tb.Near("/embedding", []float32{0.1, 0.2}, tb.L2)}, Limit: 5})
	if err != nil || len(res.Items) != 2 || res.Items[0].Distance == nil || *res.Items[0].Distance != 0.25 || res.Items[1].Distance != nil || res.NextCursor != "" {
		t.Fatalf("near = %+v, %v", res, err)
	}
}

func TestNearRejects(t *testing.T) {
	c, err := tb.New(tb.Config{BaseURL: "http://127.0.0.1:1", Token: "header.project.signature", MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	col := c.Database(parityProject, "main").Collection("users")
	near := tb.Near("/embedding", []float32{1}, "")
	for name, q := range map[string]tb.Query{
		"empty vector":   {Sort: []tb.SortKey{tb.Near("/embedding", nil, tb.Cosine)}},
		"zero cosine":    {Sort: []tb.SortKey{tb.Near("/embedding", []float32{0, 0}, "")}},
		"unknown metric": {Sort: []tb.SortKey{tb.Near("/embedding", []float32{1}, "dot")}},
		"second key":     {Sort: []tb.SortKey{near, tb.Asc("/name")}},
		"limit":          {Sort: []tb.SortKey{near}, Limit: 101},
		"cursor":         {Sort: []tb.SortKey{near}, Cursor: "next"},
	} {
		if _, err := col.Query(t.Context(), q); err == nil || !strings.Contains(err.Error(), "near") {
			t.Errorf("%s: err = %v", name, err)
		}
	}
}

func TestGeoQuery(t *testing.T) {
	const body = `{"filter":{"or":[{"within":["/loc",{"lat":-23.55,"lon":-46.63,"meters":1000}]},{"within":["/loc",{"south":-20,"west":179,"north":-10,"east":-170}]}]},"sort":[{"near":{"path":"/loc","lat":-23.55,"lon":-46.63}}],"limit":10}`
	const reply = `{"items":[{"id":"` + parityID + `","version":"opaque","state":"value","document":{},"distance":12.5}],"next_cursor":null}`
	col := parityClient(t, http.MethodPost, parityDatabase+"/collections/users/query", "", body, reply, http.StatusOK).Database(parityProject, "main").Collection("users")
	res, err := col.Query(t.Context(), tb.Query{
		Filter: tb.Or(tb.WithinRadius("/loc", -23.55, -46.63, 1000), tb.WithinBox("/loc", -20, 179, -10, -170)),
		Sort:   []tb.SortKey{tb.NearPoint("/loc", -23.55, -46.63)}, Limit: 10,
	})
	if err != nil || len(res.Items) != 1 || res.Items[0].Distance == nil || *res.Items[0].Distance != 12.5 {
		t.Fatalf("geo = %+v, %v", res, err)
	}
}

func TestAggregateQuery(t *testing.T) {
	const body = `{"filter":{"gte":["/created",1761264000000]},"aggregate":{"group_by":["/status"],"sum":["/amount"],"avg":["/amount"],"min":["/amount"],"max":["/amount"]},"limit":100}`
	const reply = `{"items":[],"next_cursor":null,"groups":[{"key":["paid"],"count":"250","sum":{"/amount":12500.5},"avg":{"/amount":50.002},"min":{"/amount":1},"max":{"/amount":null}}]}`
	col := parityClient(t, http.MethodPost, parityDatabase+"/collections/users/query", "", body, reply, http.StatusOK).Database(parityProject, "main").Collection("users")
	res, err := col.Query(t.Context(), tb.Query{
		Filter:    tb.Gte("/created", 1761264000000),
		Aggregate: &tb.Aggregate{GroupBy: []string{"/status"}, Sum: []string{"/amount"}, Avg: []string{"/amount"}, Min: []string{"/amount"}, Max: []string{"/amount"}},
		Limit:     100,
	})
	if err != nil || len(res.Items) != 0 || len(res.Groups) != 1 {
		t.Fatalf("aggregate = %+v, %v", res, err)
	}
	g := res.Groups[0]
	if len(g.Key) != 1 || string(g.Key[0]) != `"paid"` || g.Count != 250 || *g.Sum["/amount"] != 12500.5 || *g.Avg["/amount"] != 50.002 || *g.Min["/amount"] != 1 || g.Max["/amount"] != nil {
		t.Fatalf("group = %+v", g)
	}
}

func TestGeoAndAggregateRejects(t *testing.T) {
	c, err := tb.New(tb.Config{BaseURL: "http://127.0.0.1:1", Token: "header.project.signature", MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	col := c.Database(parityProject, "main").Collection("users")
	agg := &tb.Aggregate{Sum: []string{"/amount"}}
	for name, tc := range map[string]struct {
		q    tb.Query
		want string
	}{
		"radius meters":  {tb.Query{Filter: tb.WithinRadius("/loc", 0, 0, -1)}, "within"},
		"radius lat":     {tb.Query{Filter: tb.WithinRadius("/loc", 91, 0, 1)}, "within"},
		"box inverted":   {tb.Query{Filter: tb.WithinBox("/loc", 10, 0, -10, 1)}, "within"},
		"box lon":        {tb.Query{Filter: tb.WithinBox("/loc", 0, -181, 1, 1)}, "within"},
		"near point":     {tb.Query{Sort: []tb.SortKey{tb.NearPoint("/loc", math.NaN(), 0)}}, "near"},
		"near point key": {tb.Query{Sort: []tb.SortKey{tb.NearPoint("/loc", 0, 0), tb.Asc("/name")}}, "near"},
		"near limit":     {tb.Query{Sort: []tb.SortKey{tb.NearPoint("/loc", 0, 0)}, Limit: 101}, "near"},
		"agg sort":       {tb.Query{Aggregate: agg, Sort: []tb.SortKey{tb.Asc("/name")}}, "aggregate"},
		"agg project":    {tb.Query{Aggregate: agg, Project: []string{"/name"}}, "aggregate"},
		"agg cursor":     {tb.Query{Aggregate: agg, Cursor: "next"}, "aggregate"},
		"agg count":      {tb.Query{Aggregate: agg, Count: true}, "aggregate"},
		"agg group_by":   {tb.Query{Aggregate: &tb.Aggregate{GroupBy: []string{"/a", "/b", "/c", "/d", "/e"}}}, "aggregate"},
	} {
		if _, err := col.Query(t.Context(), tc.q); err == nil || !strings.Contains(err.Error(), tc.want) {
			t.Errorf("%s: err = %v", name, err)
		}
	}
}
