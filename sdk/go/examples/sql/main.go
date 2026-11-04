package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"time"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

func main() {
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	project := os.Getenv("TERRASCALE_PROJECT")
	tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: os.Getenv("TERRASCALE_API_KEY"), Project: project})
	if err != nil {
		log.Fatal(err)
	}
	c, err := v1.New(v1.Config{TokenSource: tokens})
	if err != nil {
		log.Fatal(err)
	}
	if err := run(ctx, c.Database(project, os.Getenv("TERRASCALE_DATABASE"))); err != nil {
		log.Fatal(err)
	}
}

func run(ctx context.Context, db *v1.Database) error {
	table := "sdk_go_sample_" + v1.NewID()
	if _, err := db.Execute(ctx, "CREATE TABLE "+table+" (id TEXT PRIMARY KEY, name TEXT NOT NULL, avatar BLOB)"); err != nil {
		return fmt.Errorf("create: %w", err)
	}
	defer db.Execute(context.Background(), "DROP TABLE "+table)
	id := v1.NewID()
	if _, err := db.Execute(ctx, "INSERT INTO "+table+" VALUES (?, ?, ?)", id, "Ada", []byte{1, 2, 3}); err != nil {
		return fmt.Errorf("insert: %w", err)
	}
	type person struct {
		ID     string `json:"id"`
		Name   string `json:"name"`
		Avatar []byte `json:"avatar"`
	}
	people, err := v1.QueryAs[person](ctx, db, "SELECT id, name, avatar FROM "+table+" WHERE id = ?", id)
	if err != nil || len(people) != 1 || people[0].Name != "Ada" || string(people[0].Avatar) != "\x01\x02\x03" {
		return fmt.Errorf("query: %v %w", people, err)
	}
	results, err := db.Batch(ctx, []v1.Statement{
		{SQL: "UPDATE " + table + " SET name = :name WHERE id = :id", Named: map[string]any{"name": "Grace", "id": id}},
		{SQL: "SELECT count(*) AS n, max(name) AS name FROM " + table},
	})
	if err != nil {
		return fmt.Errorf("batch: %w", err)
	}
	var n int64
	var name string
	if err := results[1].Rows[0].Scan(&n, &name); err != nil || n != 1 || name != "Grace" {
		return fmt.Errorf("batch read: n=%d name=%s %w", n, name, err)
	}
	_, err = db.Execute(ctx, "INSERT INTO "+table+" VALUES (?, 'dup', NULL)", id)
	var sqlErr *v1.SQLError
	if !errors.As(err, &sqlErr) {
		return fmt.Errorf("duplicate insert answered %v", err)
	}
	fmt.Printf("ok: insert, typed query, batch, refusal %s statement %d sqlstate %q\n", string(sqlErr.Code), sqlErr.Statement, sqlErr.SQLState)
	return nil
}
