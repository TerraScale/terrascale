package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"

	v1 "terrascale.tech/sdk/v1"
)

func sessionFault(ctx context.Context) error {
	origin := os.Getenv("E2E_SESSION_PROXY")
	client, err := v1.New(v1.Config{APIKey: os.Getenv("TERRASCALE_API_KEY"), TSAuthBaseURL: "https://auth.terrascale.tech", BaseURL: origin})
	if err != nil {
		return err
	}
	db := client.Database(os.Getenv("TERRASCALE_PROJECT_ID"), os.Getenv("TERRASCALE_DATABASE_NAME"))
	table, mode := os.Getenv("E2E_SESSION_TABLE"), os.Getenv("E2E_SESSION_FAULT")
	if _, err := db.Execute(ctx, "CREATE TABLE "+table+" (n INTEGER)"); err != nil {
		return err
	}
	defer db.Execute(ctx, "DROP TABLE "+table)
	session, err := db.Connect(ctx)
	if err != nil {
		return err
	}
	defer session.Close()
	if mode != "autocommit" && mode != "batch" {
		if err := session.Begin(ctx); err != nil {
			return err
		}
		if _, err := session.Execute(ctx, "INSERT INTO "+table+" VALUES (1)"); err != nil {
			return err
		}
	}
	if mode == "before" {
		req, err := http.NewRequestWithContext(ctx, "POST", origin+"/cut", nil)
		if err != nil {
			return err
		}
		response, err := http.DefaultClient.Do(req)
		if err != nil {
			return err
		}
		response.Body.Close()
		session.Query(ctx, "SELECT 1")
	}
	var first error
	if mode == "autocommit" {
		_, first = session.Execute(ctx, "INSERT INTO "+table+" VALUES (1)")
	} else if mode == "batch" {
		_, first = session.Batch(ctx, []v1.Statement{{SQL: "INSERT INTO " + table + " VALUES (1)"}, {SQL: "INSERT INTO " + table + " VALUES (2)"}})
	} else if mode == "read" {
		_, first = session.Query(ctx, "SELECT 1")
	} else {
		first = session.Commit(ctx)
	}
	next := session.Begin(ctx)
	rows, err := db.Query(ctx, "SELECT count(*) AS n FROM "+table)
	if err != nil {
		return err
	}
	code := func(err error) string {
		var sql *v1.SQLError
		if errors.As(err, &sql) {
			return string(sql.Code)
		}
		if errors.Is(err, v1.ErrCommitUncertain) {
			return "commit_uncertain"
		}
		if err == nil {
			return "accepted"
		}
		return err.Error()
	}
	out, err := json.Marshal(map[string]string{"first": code(first), "next": code(next), "rows": fmt.Sprint(rows[0].Values()[0])})
	if err == nil {
		fmt.Println(string(out))
	}
	return err
}
