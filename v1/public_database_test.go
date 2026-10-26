package v1_test

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestPublicDatabaseContracts(t *testing.T) {
	const project = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
	const id = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	const token = "header.project.signature"
	const statement = "SELECT * FROM users LIMIT 1"
	for _, endpoint := range []string{"info", "list", "sql", "check", "explain"} {
		t.Run(endpoint, func(t *testing.T) {
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				path := "/proxy/v1/projects/" + project + "/databases/main"
				method := http.MethodGet
				switch endpoint {
				case "list":
					path = "/proxy/v1/projects/" + project + "/databases"
				case "sql":
					path += "/sql"
					method = http.MethodPost
				case "check", "explain":
					path += "/sql/" + endpoint
					method = http.MethodPost
				}
				if r.URL.Path != path || r.Method != method || r.Header.Get("Authorization") != "Bearer "+token || r.Header.Get("X-Terrabase-User-Id") != "" {
					t.Errorf("request: %s %s auth=%q", r.Method, r.URL.Path, r.Header.Get("Authorization"))
				}
				if method == http.MethodPost {
					var body struct {
						SQL string `json:"sql"`
					}
					if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.SQL != statement {
						t.Errorf("SQL body: %v %+v", err, body)
					}
				}
				w.Header().Set("Content-Type", "application/json")
				switch endpoint {
				case "info":
					io.WriteString(w, `{"id":"`+id+`","projectId":"`+project+`","name":"main"}`)
				case "list":
					if r.URL.Query().Get("after") != "opaque+/=" || r.URL.Query().Get("limit") != "2" {
						t.Errorf("pagination: %s", r.URL.RawQuery)
					}
					io.WriteString(w, `{"items":[{"id":"`+id+`","projectId":"`+project+`","name":"main"}],"next":"next"}`)
				case "sql":
					io.WriteString(w, `{"matched":1,"truncated":true,"rows":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","value":{"id":"application-id","count":9007199254740993}}]}`)
				default:
					io.WriteString(w, `{"ok":false,"error":"unsupported SQL","collection":null,"hasLimit":false,"plan":[],"indexes":[],"suggestions":[]}`)
				}
			}))
			defer server.Close()
			c, err := tb.New(tb.Config{BaseURL: server.URL + "/proxy", Token: token, HTTPClient: server.Client()})
			if err != nil {
				t.Fatal(err)
			}
			d := c.Database(project, "main")
			switch endpoint {
			case "info":
				out, err := d.Info(t.Context())
				if err != nil || out.Name != "main" {
					t.Fatalf("info: %+v %v", out, err)
				}
			case "list":
				out, err := c.Databases(t.Context(), project, tb.PageOptions{After: "opaque+/=", Limit: 2})
				if err != nil || len(out.Items) != 1 || out.Next != "next" {
					t.Fatalf("list: %+v %v", out, err)
				}
			case "sql":
				out, err := d.SQL(t.Context(), statement)
				if err != nil || !out.Truncated || out.Rows[0].ID != "01ARZ3NDEKTSV4RRFFQ69G5FD2" || string(out.Rows[0].Value) != `{"id":"application-id","count":9007199254740993}` {
					t.Fatalf("SQL: %+v %v", out, err)
				}
			default:
				var out *tb.SQLCheck
				if endpoint == "check" {
					out, err = d.CheckSQL(t.Context(), statement)
				} else {
					out, err = d.ExplainSQL(t.Context(), statement)
				}
				if err != nil || out.OK || out.Error == nil || *out.Error != "unsupported SQL" || out.Collection != nil {
					t.Fatalf("check: %+v %v", out, err)
				}
			}
			if calls != 1 {
				t.Fatalf("calls: %d", calls)
			}
		})
	}
}

func TestPublicSQLPropagatesCredentialDenials(t *testing.T) {
	// These fixtures verify SDK error propagation, not server JWT enforcement.
	for _, status := range []int{http.StatusUnauthorized, http.StatusForbidden, http.StatusNotFound} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				w.WriteHeader(status)
				io.WriteString(w, `{"error":{"code":"not_found","message":"denied"}}`)
			}))
			defer server.Close()
			c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database("01ARZ3NDEKTSV4RRFFQ69G5FAV", "main").SQL(t.Context(), "SELECT * FROM users")
			var api *tb.APIError
			if !errors.As(err, &api) || api.Status != status || calls != 1 {
				t.Fatalf("denial: %v calls=%d", err, calls)
			}
		})
	}
}

func TestPublicUsageContracts(t *testing.T) {
	for _, database := range []string{"", "main"} {
		t.Run(database, func(t *testing.T) {
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				path := "/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV"
				if database != "" {
					path += "/databases/" + database
				}
				if r.URL.Path != path+"/usage" || r.Method != http.MethodGet || r.URL.Query().Get("granularity") != "hour" || r.Header.Get("Authorization") != "Bearer header.project.signature" {
					t.Errorf("request: %s %s", r.Method, r.URL)
				}
				io.WriteString(w, `{"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","storageBytes":0,"documents":0,"writes":0,"reads":9007199254740993,"period":{"reads":0}}`)
			}))
			defer server.Close()
			c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
			if err != nil {
				t.Fatal(err)
			}
			var out *tb.Usage
			opts := tb.UsageOptions{Granularity: "hour"}
			if database == "" {
				out, err = c.ProjectUsage(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV", opts)
			} else {
				out, err = c.Database("01ARZ3NDEKTSV4RRFFQ69G5FAV", database).Usage(t.Context(), opts)
			}
			if err != nil || out.Reads != 9007199254740993 || out.Period == nil || out.Period.Reads != 0 {
				t.Fatalf("usage: %+v %v", out, err)
			}
			if _, err = c.ProjectUsage(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV", tb.UsageOptions{Granularity: "minute"}); err == nil {
				t.Fatal("accepted invalid granularity")
			}
			if calls != 1 {
				t.Fatalf("calls: %d", calls)
			}
		})
	}
}

func TestPublicResponsesRequireContractFields(t *testing.T) {
	for _, tc := range []struct {
		name, body string
		call       func(*tb.Client, *testing.T) error
	}{
		{"list missing", `{}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.Databases(t.Context(), metricsProject, tb.PageOptions{})
			return err
		}},
		{"list null", `{"items":null}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.Databases(t.Context(), metricsProject, tb.PageOptions{})
			return err
		}},
		{"SQL missing", `{}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.Database(metricsProject, "main").SQL(t.Context(), "SELECT * FROM users")
			return err
		}},
		{"SQL null", `{"rows":null}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.Database(metricsProject, "main").SQL(t.Context(), "SELECT * FROM users")
			return err
		}},
		{"check missing", `{}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.Database(metricsProject, "main").CheckSQL(t.Context(), "SELECT * FROM users")
			return err
		}},
		{"explain null", `{"ok":null}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.Database(metricsProject, "main").ExplainSQL(t.Context(), "SELECT * FROM users")
			return err
		}},
		{"usage missing", `{}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.ProjectUsage(t.Context(), metricsProject, tb.UsageOptions{})
			return err
		}},
		{"usage null", `{"storageBytes":0,"documents":0,"reads":null,"writes":0}`, func(c *tb.Client, t *testing.T) error {
			_, err := c.Database(metricsProject, "main").Usage(t.Context(), tb.UsageOptions{})
			return err
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { io.WriteString(w, tc.body) }))
			defer server.Close()
			c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
			if err != nil {
				t.Fatal(err)
			}
			if err := tc.call(c, t); err == nil {
				t.Fatal("accepted missing/null required fields")
			}
		})
	}
}
