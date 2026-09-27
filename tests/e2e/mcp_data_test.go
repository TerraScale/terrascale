package e2e

import (
	"slices"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

type mcpDocument struct {
	ID    string         `json:"id"`
	Value map[string]any `json:"value"`
}

// mcpDatabase creates a ready database through MCP and returns its ID.
func mcpDatabase(t *testing.T, session *mcp.ClientSession) string {
	t.Helper()
	var created struct {
		ID string `json:"id"`
	}
	callTool(t, session, "create_database", map[string]any{"name": uniqueName("mcp-data"), "region": "GRU"}, &created)
	return created.ID
}

// TestMCPDataDocumentsSchemaIndexesAndSQL manages documents, schemas,
// indexes, SQL, and saved queries of a new database through MCP tools.
func TestMCPDataDocumentsSchemaIndexesAndSQL(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-data"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "data assistant"))
	databaseID := mcpDatabase(t, session)
	users := map[string]any{"databaseId": databaseID, "collection": "users"}
	with := func(extra map[string]any) map[string]any {
		arguments := map[string]any{"databaseId": databaseID, "collection": "users"}
		for key, value := range extra {
			arguments[key] = value
		}
		return arguments
	}

	callTool(t, session, "create_document", with(map[string]any{"documentId": "usr_ada", "document": map[string]any{"name": "Ada", "age": 36}}), nil)
	callTool(t, session, "create_document", with(map[string]any{"documentId": "usr_alan", "document": map[string]any{"name": "Alan"}}), nil)
	if message := callToolError(t, session, "create_document", with(map[string]any{"documentId": "usr_ada", "document": map[string]any{"name": "Again"}})); !strings.Contains(message, "already exists") {
		t.Errorf("duplicate create_document error = %q, want already exists", message)
	}
	callTool(t, session, "patch_document", with(map[string]any{"documentId": "usr_ada", "patch": map[string]any{"age": 37, "team": "engines"}}), nil)
	var ada mcpDocument
	callTool(t, session, "get_document", with(map[string]any{"documentId": "usr_ada"}), &ada)
	if ada.Value["name"] != "Ada" || ada.Value["age"] != 37.0 || ada.Value["team"] != "engines" {
		t.Errorf("patched document = %+v, want Ada aged 37 on engines", ada.Value)
	}
	callTool(t, session, "replace_document", with(map[string]any{"documentId": "usr_alan", "document": map[string]any{"name": "Alan Turing"}}), nil)

	var listed struct{ Documents []mcpDocument }
	callTool(t, session, "list_documents", with(map[string]any{"search": "turing"}), &listed)
	if len(listed.Documents) != 1 || listed.Documents[0].ID != "usr_alan" {
		t.Errorf("list_documents search = %+v, want only usr_alan", listed.Documents)
	}
	callTool(t, session, "list_documents", with(map[string]any{"limit": 1, "after": "usr_ada"}), &listed)
	if len(listed.Documents) != 1 || listed.Documents[0].ID != "usr_alan" {
		t.Errorf("list_documents page after usr_ada = %+v, want usr_alan", listed.Documents)
	}
	var collections struct{ Collections []string }
	callTool(t, session, "list_collections", map[string]any{"databaseId": databaseID}, &collections)
	if !slices.Contains(collections.Collections, "users") {
		t.Errorf("list_collections = %v, want users", collections.Collections)
	}

	callTool(t, session, "delete_document", with(map[string]any{"documentId": "usr_alan"}), nil)
	var history struct {
		Versions []struct {
			Revision string
			Deleted  bool
		}
	}
	callTool(t, session, "get_document_history", with(map[string]any{"documentId": "usr_alan"}), &history)
	if len(history.Versions) != 3 || !history.Versions[2].Deleted || history.Versions[0].Deleted {
		t.Errorf("get_document_history = %+v, want create, replace, and a deletion", history.Versions)
	}

	schema := map[string]any{
		"name": "users", "historyRetentionDays": 30, "mergePolicy": "last-write-wins",
		"fields": []any{map[string]any{"name": "name", "type": "string", "required": true, "indexed": false}},
	}
	callTool(t, session, "set_collection_schema", with(map[string]any{"schema": schema}), nil)
	var stored struct{ Schema map[string]any }
	callTool(t, session, "get_collection_schema", users, &stored)
	if stored.Schema["name"] != "users" {
		t.Errorf("get_collection_schema = %+v, want the saved users schema", stored.Schema)
	}
	if message := callToolError(t, session, "set_collection_schema", with(map[string]any{"schema": map[string]any{"name": "orders"}})); !strings.Contains(message, "invalid input") {
		t.Errorf("mismatched set_collection_schema error = %q, want invalid input", message)
	}

	callTool(t, session, "create_index", with(map[string]any{"field": "age"}), nil)
	var indexes struct {
		Indexes []struct{ Field, State string }
	}
	callTool(t, session, "list_indexes", users, &indexes)
	if len(indexes.Indexes) != 1 || indexes.Indexes[0].Field != "age" {
		t.Errorf("list_indexes = %+v, want age", indexes.Indexes)
	}

	var analysis struct {
		OK          bool
		Suggestions []struct{ Field string }
	}
	callTool(t, session, "check_sql", map[string]any{"databaseId": databaseID, "sql": "SELECT * FROM users WHERE team = 'engines'"}, &analysis)
	if !analysis.OK || len(analysis.Suggestions) != 1 || analysis.Suggestions[0].Field != "team" {
		t.Errorf("check_sql = %+v, want an accepted query suggesting team", analysis)
	}
	callTool(t, session, "check_sql", map[string]any{"databaseId": databaseID, "sql": "DELETE FROM users"}, &analysis)
	if analysis.OK {
		t.Error("check_sql accepted a DELETE statement")
	}
	var result struct {
		Result struct{ Rows []mcpDocument }
	}
	callTool(t, session, "run_sql", map[string]any{"databaseId": databaseID, "sql": "SELECT * FROM users LIMIT 5"}, &result)
	if len(result.Result.Rows) != 1 || result.Result.Rows[0].ID != "usr_ada" {
		t.Errorf("run_sql rows = %+v, want usr_ada", result.Result.Rows)
	}
	if message := callToolError(t, session, "run_sql", map[string]any{"databaseId": databaseID, "sql": "DROP TABLE users"}); !strings.Contains(message, "read-only") {
		t.Errorf("run_sql DROP error = %q, want a read-only rejection", message)
	}

	var saved struct{ ID string }
	callTool(t, session, "save_query", map[string]any{"name": "All users", "sql": "SELECT * FROM users"}, &saved)
	var queries struct{ Queries []struct{ ID, Name string } }
	callTool(t, session, "list_saved_queries", nil, &queries)
	if len(queries.Queries) != 1 || queries.Queries[0].ID != saved.ID {
		t.Errorf("list_saved_queries = %+v, want %s", queries.Queries, saved.ID)
	}
	callTool(t, session, "delete_saved_query", map[string]any{"queryId": saved.ID}, nil)
	callTool(t, session, "list_saved_queries", nil, &queries)
	if len(queries.Queries) != 0 {
		t.Errorf("list_saved_queries after delete = %+v, want none", queries.Queries)
	}

	if message := callToolError(t, session, "list_documents", map[string]any{"databaseId": databaseID, "collection": "bad/name"}); !strings.Contains(message, "collection") {
		t.Errorf("invalid collection error = %q, want a collection rule", message)
	}
	if message := callToolError(t, session, "get_document", map[string]any{"databaseId": "db_missing", "collection": "users", "documentId": "usr_ada"}); !strings.Contains(message, "not found") {
		t.Errorf("foreign database error = %q, want not found", message)
	}
}

// TestMCPSampleIndexRecommendationAndMigration reads and applies the seeded
// index recommendation and migration of the sample account's orders.
func TestMCPSampleIndexRecommendationAndMigration(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	session := connectMCP(t, createAccessToken(t, page, sampleAccount.Password, "sample index assistant"))
	const databaseID = "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC"

	var recommendations struct {
		Recommendations []struct {
			ID     string
			Fields []string
		}
	}
	callTool(t, session, "list_index_recommendations", map[string]any{"databaseId": databaseID, "collection": "orders"}, &recommendations)
	if len(recommendations.Recommendations) != 1 || !slices.Equal(recommendations.Recommendations[0].Fields, []string{"total"}) {
		t.Fatalf("list_index_recommendations = %+v, want the seeded total recommendation", recommendations.Recommendations)
	}
	var applied struct{ Status, Collection string }
	callTool(t, session, "apply_index_recommendation", map[string]any{"recommendationId": recommendations.Recommendations[0].ID}, &applied)
	if applied.Collection != "orders" || (applied.Status != "applied" && applied.Status != "already-applied") {
		t.Errorf("apply_index_recommendation = %+v, want orders applied", applied)
	}
	var indexes struct {
		Indexes []struct{ Field, State string }
	}
	callTool(t, session, "list_indexes", map[string]any{"databaseId": databaseID, "collection": "orders"}, &indexes)
	if !slices.ContainsFunc(indexes.Indexes, func(index struct{ Field, State string }) bool {
		return index.Field == "total" && index.State == "building"
	}) {
		t.Errorf("list_indexes = %+v, want total building", indexes.Indexes)
	}

	var migration struct {
		Migration *struct {
			State   string
			Percent int
		}
	}
	callTool(t, session, "get_index_migration", map[string]any{"databaseId": databaseID, "collection": "orders"}, &migration)
	if migration.Migration == nil || migration.Migration.State != "running" || migration.Migration.Percent != 69 {
		t.Errorf("get_index_migration = %+v, want the running 69%% sample", migration.Migration)
	}
}

// TestMCPBackupsAndQuerySubscriptions backs up and restores a database and
// manages shared-result configurations through MCP tools.
func TestMCPBackupsAndQuerySubscriptions(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	// The restore adds a second database, past the Free plan's one.
	registerAndOnboard(t, page, newAccount("mcp-backups"), "starter")
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "backup assistant"))
	databaseID := mcpDatabase(t, session)
	callTool(t, session, "create_document", map[string]any{
		"databaseId": databaseID, "collection": "orders", "documentId": "ord_1", "document": map[string]any{"total": 42},
	}, nil)

	var backup struct {
		ID        string
		Status    string
		SizeBytes int64
	}
	callTool(t, session, "create_backup", map[string]any{"databaseId": databaseID}, &backup)
	if backup.Status != "ready" || backup.SizeBytes == 0 {
		t.Errorf("create_backup = %+v, want a ready non-empty backup", backup)
	}
	if message := callToolError(t, session, "set_backup_policy", map[string]any{"databaseId": databaseID, "fullBackupCron": "0 3 * * *", "incrementalBackupCron": "0 */6 * * *", "retentionDays": 400}); !strings.Contains(message, "retention") {
		t.Errorf("set_backup_policy retention error = %q, want the retention rule", message)
	}
	callTool(t, session, "set_backup_policy", map[string]any{"databaseId": databaseID, "fullBackupCron": "0 3 * * *", "incrementalBackupCron": "0 */6 * * *", "retentionDays": 14}, nil)
	var listing struct {
		Policy  struct{ FullBackupCron string }
		Backups []struct{ ID string }
	}
	callTool(t, session, "list_backups", map[string]any{"databaseId": databaseID}, &listing)
	if listing.Policy.FullBackupCron != "0 3 * * *" || len(listing.Backups) != 1 || listing.Backups[0].ID != backup.ID {
		t.Errorf("list_backups = %+v, want the saved policy and backup %s", listing, backup.ID)
	}

	var restored struct{ DatabaseID, Status string }
	restoredName := uniqueName("restored")
	callTool(t, session, "restore_backup", map[string]any{"databaseId": databaseID, "backupId": backup.ID, "name": restoredName}, &restored)
	if restored.Status != "ready" {
		t.Errorf("restore_backup = %+v, want a ready database", restored)
	}
	var order mcpDocument
	callTool(t, session, "get_document", map[string]any{"databaseId": restored.DatabaseID, "collection": "orders", "documentId": "ord_1"}, &order)
	if order.Value["total"] != 42.0 {
		t.Errorf("restored document = %+v, want total 42", order.Value)
	}

	subscription := map[string]any{"databaseId": databaseID, "name": "big-orders", "query": "SELECT * FROM orders LIMIT 10", "transport": "carrier-pigeon"}
	if message := callToolError(t, session, "attach_query_subscription", subscription); !strings.Contains(message, "/properties/transport") {
		t.Errorf("attach_query_subscription transport error = %q, want the transport enum", message)
	}
	subscription["transport"] = "websocket"
	var attached struct{ ID, DatabaseName, Status string }
	callTool(t, session, "attach_query_subscription", subscription, &attached)
	if attached.Status != "saved" || attached.DatabaseName == "" {
		t.Errorf("attach_query_subscription = %+v, want a saved configuration", attached)
	}
	callTool(t, session, "pause_query_subscription", map[string]any{"subscriptionId": attached.ID}, nil)
	var subscriptions struct{ Subscriptions []struct{ ID, Status string } }
	callTool(t, session, "list_query_subscriptions", nil, &subscriptions)
	if len(subscriptions.Subscriptions) != 1 || subscriptions.Subscriptions[0].Status != "paused" {
		t.Errorf("list_query_subscriptions = %+v, want one paused configuration", subscriptions.Subscriptions)
	}
	callTool(t, session, "resume_query_subscription", map[string]any{"subscriptionId": attached.ID}, nil)
	callTool(t, session, "list_query_subscriptions", nil, &subscriptions)
	if len(subscriptions.Subscriptions) != 1 || subscriptions.Subscriptions[0].Status != "saved" {
		t.Errorf("list_query_subscriptions after resume = %+v, want one saved configuration", subscriptions.Subscriptions)
	}
	callTool(t, session, "detach_query_subscription", map[string]any{"subscriptionId": attached.ID}, nil)
	callTool(t, session, "list_query_subscriptions", nil, &subscriptions)
	if len(subscriptions.Subscriptions) != 0 {
		t.Errorf("list_query_subscriptions after detach = %+v, want none", subscriptions.Subscriptions)
	}
}
