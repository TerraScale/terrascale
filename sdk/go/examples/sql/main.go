package main

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"log"
	"os"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func main() {
	if err := run(); err != nil {
		log.Fatal(err)
	}
}

func run() error {
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	c, err := v1.New(v1.Config{Token: os.Getenv("TERRASCALE_API_KEY")})
	if err != nil {
		return err
	}
	db := c.Database(os.Getenv("TERRASCALE_PROJECT"), os.Getenv("TERRASCALE_DATABASE"))
	table := "sdk_go_sample_" + v1.NewID()
	if _, err := db.Execute(ctx, "CREATE TABLE "+table+" (id TEXT PRIMARY KEY, name TEXT NOT NULL, avatar BLOB)"); err != nil {
		return fmt.Errorf("create: %w", err)
	}
	defer db.Execute(context.Background(), "DROP TABLE "+table)
	id := v1.NewID()
	if _, err := db.Execute(ctx, "INSERT INTO "+table+" VALUES (?, :name, ?)", id, sql.Named("name", "Ada"), []byte{1, 2, 3}); err != nil {
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
		{SQL: "UPDATE " + table + " SET name = ? WHERE id = ?", Args: []any{"Grace", id}},
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
	fmt.Printf("ok: insert, typed query, batch, refusal %s statement %d sqlstate %q\n", sqlErr.Code, sqlErr.Statement, sqlErr.SQLState)
	return nil
}
