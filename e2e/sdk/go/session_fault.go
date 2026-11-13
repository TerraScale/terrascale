package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"strings"
	"sync"

	v1 "terrascale.tech/sdk/v1"
)

type orderedContext struct {
	context.Context
	once   sync.Once
	paused chan struct{}
	resume chan struct{}
}

func (c *orderedContext) Done() <-chan struct{} {
	c.once.Do(func() {
		close(c.paused)
		<-c.resume
	})
	return c.Context.Done()
}

func sessionFault(ctx context.Context) error {
	origin := os.Getenv("E2E_SESSION_PROXY")
	client, err := v1.New(v1.Config{APIKey: os.Getenv("TERRASCALE_API_KEY"), TSAuthBaseURL: "https://auth.terrascale.tech", BaseURL: origin})
	if err != nil {
		return err
	}
	db := client.Database(os.Getenv("TERRASCALE_PROJECT_ID"), os.Getenv("TERRASCALE_DATABASE_NAME"))
	table, mode := os.Getenv("E2E_SESSION_TABLE"), os.Getenv("E2E_SESSION_FAULT")
	control := func(path string) error {
		req, err := http.NewRequestWithContext(ctx, "POST", origin+path, nil)
		if err != nil {
			return err
		}
		response, err := http.DefaultClient.Do(req)
		if err != nil {
			return err
		}
		defer response.Body.Close()
		if response.StatusCode != 200 {
			return fmt.Errorf("proxy control: %d", response.StatusCode)
		}
		return nil
	}
	code := func(err error) string {
		if err == nil {
			return "accepted"
		}
		if errors.Is(err, errors.New("unrelated session sentinel")) {
			panic("unrelated sentinel matched")
		}
		uncertain := errors.Is(err, v1.ErrCommitUncertain)
		var sql *v1.SQLError
		if errors.As(err, &sql) {
			if uncertain != (sql.Code == v1.ErrCommitUncertain || sql.Code == v1.ErrWriteOutcomeUnknown) {
				panic("SQL uncertainty mismatch")
			}
			return string(sql.Code)
		}
		if uncertain {
			return "commit_uncertain"
		}
		var api *v1.APIError
		if errors.As(err, &api) {
			return string(api.Code)
		}
		if mode == "read" || mode == "overtake-begin" {
			var network *net.OpError
			if !errors.Is(err, io.EOF) && !errors.Is(err, net.ErrClosed) && !errors.As(err, &network) {
				panic(fmt.Sprintf("unexpected transport error %T: %v", err, err))
			}
			return "transport"
		}
		panic(fmt.Sprintf("unexpected error type %T: %v", err, err))
	}
	if _, err := db.Execute(ctx, "CREATE TABLE "+table+" (n INTEGER)"); err != nil {
		return err
	}
	defer db.Execute(ctx, "DROP TABLE "+table)
	if mode == "retry" {
		if err := control("/arm-http"); err != nil {
			return err
		}
		_, read := db.Regions(ctx)
		_, mutation := db.Execute(ctx, "INSERT INTO "+table+" VALUES (1)")
		if err := control("/disarm-http"); err != nil {
			return err
		}
		if read != nil || !errors.Is(mutation, v1.ErrCommitUncertain) {
			return fmt.Errorf("retry policy read=%v mutation=%v", read, mutation)
		}
		var api *v1.APIError
		if !errors.As(mutation, &api) || api.Code != v1.ErrCommitUncertain || api.Status != 503 {
			return fmt.Errorf("mutation type %T", mutation)
		}
		out, _ := json.Marshal(map[string]string{"read": code(read), "mutation": code(mutation)})
		fmt.Println(string(out))
		return nil
	}
	session, err := db.Connect(ctx)
	if err != nil {
		return err
	}
	defer session.Close()
	if mode != "autocommit" && mode != "batch" && mode != "overtake-begin" {
		if err := session.Begin(ctx); err != nil {
			return err
		}
		if _, err := session.Execute(ctx, "INSERT INTO "+table+" VALUES (1)"); err != nil {
			return err
		}
	}
	if mode == "before" {
		if err := control("/cut"); err != nil {
			return err
		}
		session.Query(ctx, "SELECT 1")
	}
	var first error
	if strings.HasPrefix(mode, "overtake-") {
		delayed := &orderedContext{Context: ctx, paused: make(chan struct{}), resume: make(chan struct{})}
		answer := make(chan error, 1)
		go func() {
			if mode == "overtake-batch" {
				_, err := session.Batch(delayed, []v1.Statement{{SQL: "INSERT INTO " + table + " VALUES (2)"}, {SQL: "INSERT INTO " + table + " VALUES (3)"}})
				answer <- err
			} else {
				_, err := session.Execute(delayed, "INSERT INTO "+table+" VALUES (2)")
				answer <- err
			}
		}()
		<-delayed.paused
		switch mode {
		case "overtake-begin":
			err = session.Begin(ctx)
		case "overtake-rollback":
			err = session.Rollback(ctx)
		default:
			err = session.Commit(ctx)
		}
		close(delayed.resume)
		first = <-answer
		if err != nil {
			return err
		}
	} else if mode == "cancel" {
		caller, cancel := context.WithCancel(ctx)
		answer := make(chan error, 1)
		go func() { answer <- session.Commit(caller) }()
		if err := control("/committed"); err != nil {
			cancel()
			return err
		}
		cancel()
		first = <-answer
	} else if mode == "autocommit" {
		_, first = session.Execute(ctx, "INSERT INTO "+table+" VALUES (1)")
	} else if mode == "batch" {
		_, first = session.Batch(ctx, []v1.Statement{{SQL: "INSERT INTO " + table + " VALUES (1)"}, {SQL: "INSERT INTO " + table + " VALUES (2)"}})
	} else if mode == "read" {
		_, first = session.Query(ctx, "SELECT 1")
	} else {
		first = session.Commit(ctx)
	}
	firstCode := code(first)
	next := code(session.Begin(ctx))
	_, later := session.Execute(ctx, "SELECT 2")
	second := code(later)
	if mode != "before" && mode != "read" && mode != "overtake-begin" {
		if !errors.Is(first, v1.ErrCommitUncertain) {
			return fmt.Errorf("first is not uncertain: %T %v", first, first)
		}
		if !errors.Is(later, v1.ErrCommitUncertain) {
			return fmt.Errorf("later is not uncertain: %T %v", later, later)
		}
	}
	rows, err := db.Query(ctx, "SELECT count(*) AS n FROM "+table)
	if err != nil {
		return err
	}
	out, err := json.Marshal(map[string]string{"first": firstCode, "next": next, "second": second, "rows": fmt.Sprint(rows[0].Values()[0])})
	if err == nil {
		fmt.Println(string(out))
	}
	return err
}
