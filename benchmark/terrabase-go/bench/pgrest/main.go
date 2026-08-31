// pgrest is the REST/JSON document service of the PostgreSQL full-stack
// benchmark lane: PostgreSQL 18 (jsonb documents) behind pgbouncer
// (transaction pooling) behind this service. The point of the lane is that
// every request pays real JSON encode/decode plus the pooled database
// round-trip, so the handler paths marshal and unmarshal explicitly and
// cache nothing.
//
// This is a nested Go module (bench-only dependency closure: pgx) rather
// than part of the parent terrabase-go module, so the client library's
// dependency graph stays free of the bench driver's database driver.
//
// Bench artifact, not a product: loopback only, no auth, and an explicit
// POST /admin/reset that recreates the schema so each lane run starts from
// an empty store.
package main

import (
	"database/sql"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"os"
	"strconv"
	"time"

	// pgx's stdlib driver in simple-protocol mode: the extended protocol's
	// prepared statements do not survive pgbouncer's transaction-pooling
	// rerouting on PostgreSQL 18 (SQLSTATE 26000 under concurrency), and
	// simple protocol is pgx's documented pgbouncer-compatible mode.
	_ "github.com/jackc/pgx/v5/stdlib"
)

// The document contract of the lane (bench/src/lane.rs `document`): a
// name, a group, and a 256-byte payload, stored as one jsonb body.
type document struct {
	Name    string `json:"name"`
	Group   int    `json:"group"`
	Payload string `json:"payload"`
}

// The response shape of the read and query endpoints.
type documentResponse struct {
	ID      string `json:"id"`
	Name    string `json:"name"`
	Group   int    `json:"group"`
	Payload string `json:"payload"`
}

func respondJSON(writer http.ResponseWriter, status int, value any) {
	writer.Header().Set("Content-Type", "application/json")
	writer.WriteHeader(status)
	if err := json.NewEncoder(writer).Encode(value); err != nil {
		log.Printf("write response: %v", err)
	}
}

func respondError(writer http.ResponseWriter, status int, message string) {
	respondJSON(writer, status, map[string]string{"error": message})
}

// schema recreates the lane's store: documents as jsonb with an expression
// btree index covering the name-equality query family. This is the single
// source of the schema; the start script only calls this endpoint.
const schema = `
DROP TABLE IF EXISTS documents;
CREATE TABLE documents(
    tenant text NOT NULL,
    id text NOT NULL,
    body jsonb NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (tenant, id));
CREATE INDEX documents_tenant_name_idx ON documents (tenant, (body->>'name'), id);`

type service struct {
	db              *sql.DB
	postgresVersion string
}

func (s *service) handleHealth(writer http.ResponseWriter, _ *http.Request) {
	respondJSON(writer, http.StatusOK, map[string]string{
		"status":           "ok",
		"postgres_version": s.postgresVersion,
	})
}

// handleReset recreates the schema: one bench run starts from an empty
// store, exactly like the engine-side PostgreSQL driver's setup.
func (s *service) handleReset(writer http.ResponseWriter, _ *http.Request) {
	if _, err := s.db.Exec(schema); err != nil {
		respondError(writer, http.StatusInternalServerError, "reset failed: "+err.Error())
		return
	}
	respondJSON(writer, http.StatusOK, map[string]string{"status": "reset"})
}

func (s *service) handleGet(writer http.ResponseWriter, request *http.Request) {
	tenant := request.PathValue("tenant")
	id := request.PathValue("id")
	var body []byte
	err := s.db.QueryRow(
		"SELECT body FROM documents WHERE tenant = $1 AND id = $2", tenant, id,
	).Scan(&body)
	if errors.Is(err, sql.ErrNoRows) {
		respondError(writer, http.StatusNotFound, "not found")
		return
	}
	if err != nil {
		respondError(writer, http.StatusInternalServerError, err.Error())
		return
	}
	var doc document
	if err := json.Unmarshal(body, &doc); err != nil {
		respondError(writer, http.StatusInternalServerError, "stored body is not JSON: "+err.Error())
		return
	}
	respondJSON(writer, http.StatusOK, documentResponse{ID: id, Name: doc.Name, Group: doc.Group, Payload: doc.Payload})
}

func (s *service) handlePut(writer http.ResponseWriter, request *http.Request) {
	tenant := request.PathValue("tenant")
	id := request.PathValue("id")
	var doc document
	if err := json.NewDecoder(http.MaxBytesReader(writer, request.Body, 1<<20)).Decode(&doc); err != nil {
		respondError(writer, http.StatusBadRequest, "body is not a JSON document: "+err.Error())
		return
	}
	if doc.Name == "" {
		respondError(writer, http.StatusBadRequest, "name is required")
		return
	}
	stored, err := json.Marshal(doc)
	if err != nil {
		respondError(writer, http.StatusInternalServerError, err.Error())
		return
	}
	var version int64
	err = s.db.QueryRow(`
		INSERT INTO documents(tenant, id, body, version)
		VALUES ($1, $2, $3::jsonb, 1)
		ON CONFLICT (tenant, id) DO UPDATE SET
		    body = EXCLUDED.body,
		    version = documents.version + 1
		RETURNING version`, tenant, id, string(stored)).Scan(&version)
	if err != nil {
		respondError(writer, http.StatusInternalServerError, err.Error())
		return
	}
	respondJSON(writer, http.StatusOK, map[string]int64{"version": version})
}

func (s *service) handleQuery(writer http.ResponseWriter, request *http.Request) {
	tenant := request.URL.Query().Get("tenant")
	name := request.URL.Query().Get("name")
	if tenant == "" || name == "" {
		respondError(writer, http.StatusBadRequest, "tenant and name are required")
		return
	}
	limit := parseLimit(request.URL.Query().Get("limit"))
	rows, err := s.db.Query(`
		SELECT id, body FROM documents
		WHERE tenant = $1 AND body->>'name' = $2
		ORDER BY body->>'name', id
		LIMIT $3`, tenant, name, limit)
	if err != nil {
		respondError(writer, http.StatusInternalServerError, err.Error())
		return
	}
	defer rows.Close()
	results := make([]documentResponse, 0, limit)
	for rows.Next() {
		var id string
		var body []byte
		if err := rows.Scan(&id, &body); err != nil {
			respondError(writer, http.StatusInternalServerError, err.Error())
			return
		}
		var doc document
		if err := json.Unmarshal(body, &doc); err != nil {
			respondError(writer, http.StatusInternalServerError, "stored body is not JSON: "+err.Error())
			return
		}
		results = append(results, documentResponse{ID: id, Name: doc.Name, Group: doc.Group, Payload: doc.Payload})
	}
	if err := rows.Err(); err != nil {
		respondError(writer, http.StatusInternalServerError, err.Error())
		return
	}
	respondJSON(writer, http.StatusOK, results)
}

// parseLimit defaults to the lane's first-row query and caps a caller's
// larger request so one query cannot drag the whole store.
func parseLimit(raw string) int {
	if raw == "" {
		return 1
	}
	limit, err := strconv.Atoi(raw)
	if err != nil || limit < 1 {
		return 1
	}
	if limit > 100 {
		return 100
	}
	return limit
}

func main() {
	listen := getenv("PGREST_LISTEN", "127.0.0.1:55433")
	databaseURL := getenv(
		"DATABASE_URL",
		"postgres://bench@127.0.0.1:55434/postgres?sslmode=disable&default_query_exec_mode=simple_protocol",
	)
	maxConns := 32
	if raw := os.Getenv("PGREST_MAX_CONNS"); raw != "" {
		if parsed, err := strconv.Atoi(raw); err == nil && parsed > 0 {
			maxConns = parsed
		}
	}

	db, err := sql.Open("pgx", databaseURL)
	if err != nil {
		log.Fatalf("open database: %v", err)
	}
	db.SetMaxOpenConns(maxConns)
	db.SetMaxIdleConns(maxConns)
	db.SetConnMaxLifetime(0)

	// The stack may come up in any order; readiness is the first
	// successful SELECT through pgbouncer, and the version string it
	// returns becomes the identity the lane records verbatim.
	var version string
	deadline := time.Now().Add(60 * time.Second)
	for {
		err = db.QueryRow("SELECT version()").Scan(&version)
		if err == nil {
			break
		}
		if time.Now().After(deadline) {
			log.Fatalf("database never answered through %s: %v", databaseURL, err)
		}
		time.Sleep(500 * time.Millisecond)
	}

	s := &service{db: db, postgresVersion: version}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.handleHealth)
	mux.HandleFunc("POST /admin/reset", s.handleReset)
	mux.HandleFunc("GET /documents/{tenant}/{id}", s.handleGet)
	mux.HandleFunc("PUT /documents/{tenant}/{id}", s.handlePut)
	mux.HandleFunc("GET /query", s.handleQuery)

	log.Printf("pgrest listening on http://%s (database %s, max %d connections, %s)",
		listen, databaseURL, maxConns, version)
	if err := http.ListenAndServe(listen, mux); err != nil {
		log.Fatalf("serve: %v", err)
	}
}

func getenv(name, fallback string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return fallback
}
