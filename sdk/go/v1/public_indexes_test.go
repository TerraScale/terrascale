package v1_test

import (
	"net/http"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestPublicIndexContracts(t *testing.T) {
	const index = `{"name":"by_email","collection":"users","fields":["email"],"state":"active"}`
	for _, endpoint := range []string{"create", "list", "filtered list", "drop", "recommendations", "apply"} {
		t.Run(endpoint, func(t *testing.T) {
			query := ""
			method, path, body, reply, status := http.MethodGet, "/indexes", "", `{"items":[`+index+`]}`, http.StatusOK
			switch endpoint {
			case "create":
				method, body, reply, status = http.MethodPost, `{"collection":"users","fields":["email"],"name":"by_email"}`, index, http.StatusCreated
			case "filtered list":
				query = "collection=users"
			case "drop":
				method, path, reply, status = http.MethodDelete, "/indexes/by_email", "", http.StatusNoContent
			case "recommendations":
				path, reply = "/index-recommendations", `{"items":[{"id":"users:email","collection":"users","fields":["email"],"reason":"filter","queries":9007199254740993}]}`
			case "apply":
				method, path, reply = http.MethodPost, "/index-recommendations/users:email/apply", `{"status":"already-applied","index":`+index+`}`
			}
			d := parityClient(t, method, parityDatabase+path, query, body, reply, status).Database(parityProject, "main")
			switch endpoint {
			case "create":
				out, err := d.CreateIndex(t.Context(), tb.IndexRequest{Collection: "users", Fields: []string{"email"}, Name: "by_email"})
				if err != nil || out.State != "active" || out.Fields[0] != "email" {
					t.Fatalf("create = %+v, %v", out, err)
				}
			case "list", "filtered list":
				var options []tb.IndexListOptions
				if endpoint == "filtered list" {
					options = append(options, tb.IndexListOptions{Collection: "users"})
				}
				out, err := d.Indexes(t.Context(), options...)
				if err != nil || len(out) != 1 || out[0].Name != "by_email" {
					t.Fatalf("list = %+v, %v", out, err)
				}
			case "drop":
				if err := d.DropIndex(t.Context(), "by_email"); err != nil {
					t.Fatal(err)
				}
			case "recommendations":
				out, err := d.IndexRecommendations(t.Context())
				if err != nil || len(out) != 1 || out[0].Queries != 9007199254740993 {
					t.Fatalf("recommendations = %+v, %v", out, err)
				}
			case "apply":
				out, err := d.ApplyIndexRecommendation(t.Context(), "users:email")
				if err != nil || out.Status != "already-applied" || out.Index.Name != "by_email" {
					t.Fatalf("apply = %+v, %v", out, err)
				}
			}
		})
	}
}
