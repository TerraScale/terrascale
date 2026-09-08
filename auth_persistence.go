package sdk

import (
	"context"
	"encoding/json"
	"net/url"
	"strings"
)

// AuthRecord preserves exact JSON values, including 64-bit numbers.
type AuthRecord map[string]json.RawMessage
type AuthResult struct {
	Record        AuthRecord   `json:"record"`
	Matched       bool         `json:"matched"`
	Records       []AuthRecord `json:"records,omitempty"`
	TransactionID *string      `json:"transactionId,omitempty"`
	ExpiresAtMS   *uint64      `json:"expiresAtMs,omitempty"`
}
type AuthUniqueInsert struct {
	Model  string     `json:"model"`
	Record AuthRecord `json:"record"`
}
type AuthConditionalUpdate struct {
	Model    string     `json:"model"`
	ID       string     `json:"id"`
	Expected AuthRecord `json:"expected"`
	Update   AuthRecord `json:"update"`
}
type AuthConsume struct {
	Model    string     `json:"model"`
	ID       string     `json:"id"`
	Expected AuthRecord `json:"expected"`
}
type AuthUniqueIndex struct {
	Model  string   `json:"model"`
	Fields []string `json:"fields"`
}
type AuthPredicate struct {
	Field     string          `json:"field"`
	Value     json.RawMessage `json:"value"`
	Operator  string          `json:"operator,omitempty"`
	Connector string          `json:"connector,omitempty"`
	Mode      string          `json:"mode,omitempty"`
}
type AuthPageQuery struct {
	Model      string          `json:"model"`
	Predicates []AuthPredicate `json:"predicates"`
	Offset     uint32          `json:"offset"`
	Limit      uint32          `json:"limit"`
}
type AuthPredicateOnly struct {
	Model      string          `json:"model"`
	Predicates []AuthPredicate `json:"predicates"`
}
type AuthPredicateUpdate struct {
	Model      string          `json:"model"`
	Predicates []AuthPredicate `json:"predicates"`
	Update     AuthRecord      `json:"update"`
}
type AuthIncrement struct {
	Model      string             `json:"model"`
	Predicates []AuthPredicate    `json:"predicates"`
	Increment  map[string]float64 `json:"increment"`
	Set        AuthRecord         `json:"set,omitempty"`
}

// AuthPersistenceOptions always names one stable operation identity. TransactionID
// binds writes/findPage to an existing transaction; record GET is quorum-read only.
type AuthPersistenceOptions struct {
	IdempotencyKey string
	TransactionID  string
}

// AuthPersistenceClient exposes strict public persistence operations, not a
// framework callback adapter. Uncertain mutations are returned to the caller;
// resolve them with the same identity rather than replaying application code.
type AuthPersistenceClient struct{ c *Client }

func (a *AuthPersistenceClient) mutate(ctx context.Context, database, operation, model string, body any, options AuthPersistenceOptions) (*AuthResult, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	payload, err := json.Marshal(body)
	if err != nil {
		return nil, err
	}
	if len(payload) > 1<<20 {
		return nil, &ValidationError{Field: "auth request", Message: "must not exceed 1 MiB"}
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if model == "" && !strings.HasPrefix(operation, "transactions/") {
		return nil, &ValidationError{Field: "model", Message: "must not be blank"}
	}
	if model != "" {
		if err := requireID("model", model); err != nil {
			return nil, err
		}
	}
	opts := []RequestOption{WithIdempotencyKey(options.IdempotencyKey)}
	if err := requiredManagementMutation(opts); err != nil {
		return nil, err
	}
	if len(options.TransactionID) > 128 || strings.ContainsAny(options.TransactionID, "\r\n\x00") {
		return nil, &ValidationError{Field: "transaction_id", Message: "must be at most 128 bytes without control characters"}
	}
	opts = append(opts, func(rc *requestConfig) { rc.authTransaction = options.TransactionID; rc.noRetry = true })
	var out AuthResult
	if err := a.c.do(ctx, "POST", path+"/auth/"+operation, nil, json.RawMessage(payload), &out, "auth."+operation, opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
func (a *AuthPersistenceClient) InsertUnique(ctx context.Context, database string, request AuthUniqueInsert, options AuthPersistenceOptions) (*AuthResult, error) {
	if request.Record == nil {
		return nil, &ValidationError{Field: "record", Message: "must be an object"}
	}
	return a.mutate(ctx, database, "insertUnique", request.Model, request, options)
}
func (a *AuthPersistenceClient) CompareAndSwap(ctx context.Context, database string, request AuthConditionalUpdate, options AuthPersistenceOptions) (*AuthResult, error) {
	if err := requireID("id", request.ID); err != nil {
		return nil, err
	}
	if request.Expected == nil || request.Update == nil {
		return nil, &ValidationError{Field: "expected/update", Message: "must be objects"}
	}
	return a.mutate(ctx, database, "compareAndSwap", request.Model, request, options)
}
func (a *AuthPersistenceClient) ConsumeOne(ctx context.Context, database string, request AuthConsume, options AuthPersistenceOptions) (*AuthResult, error) {
	if err := requireID("id", request.ID); err != nil {
		return nil, err
	}
	if request.Expected == nil {
		return nil, &ValidationError{Field: "expected", Message: "must be an object"}
	}
	return a.mutate(ctx, database, "consumeOne", request.Model, request, options)
}
func (a *AuthPersistenceClient) ConsumeMatching(ctx context.Context, database string, request AuthPredicateOnly, options AuthPersistenceOptions) (*AuthResult, error) {
	return a.mutate(ctx, database, "consumeMatching", request.Model, request, options)
}
func (a *AuthPersistenceClient) Count(ctx context.Context, database string, request AuthPredicateOnly, options AuthPersistenceOptions) (*AuthResult, error) {
	return a.mutate(ctx, database, "count", request.Model, request, options)
}
func (a *AuthPersistenceClient) DeleteMany(ctx context.Context, database string, request AuthPredicateOnly, options AuthPersistenceOptions) (*AuthResult, error) {
	return a.mutate(ctx, database, "deleteMany", request.Model, request, options)
}
func (a *AuthPersistenceClient) DeleteOne(ctx context.Context, database string, request AuthPredicateOnly, options AuthPersistenceOptions) (*AuthResult, error) {
	return a.mutate(ctx, database, "deleteOne", request.Model, request, options)
}
func (a *AuthPersistenceClient) IncrementOne(ctx context.Context, database string, request AuthIncrement, options AuthPersistenceOptions) (*AuthResult, error) {
	return a.mutate(ctx, database, "incrementOne", request.Model, request, options)
}
func (a *AuthPersistenceClient) Index(ctx context.Context, database string, request AuthUniqueIndex, options AuthPersistenceOptions) (*AuthResult, error) {
	if len(request.Fields) == 0 {
		return nil, &ValidationError{Field: "fields", Message: "must not be empty"}
	}
	return a.mutate(ctx, database, "index", request.Model, request, options)
}
func (a *AuthPersistenceClient) FindPage(ctx context.Context, database string, request AuthPageQuery, options AuthPersistenceOptions) (*AuthResult, error) {
	if request.Limit > 1000 || request.Offset > 1000 {
		return nil, &ValidationError{Field: "pagination", Message: "offset and limit must not exceed 1000"}
	}
	if request.Predicates == nil {
		request.Predicates = []AuthPredicate{}
	}
	return a.mutate(ctx, database, "findPage", request.Model, request, options)
}
func (a *AuthPersistenceClient) UpdateMany(ctx context.Context, database string, request AuthPredicateUpdate, options AuthPersistenceOptions) (*AuthResult, error) {
	return a.mutate(ctx, database, "updateMany", request.Model, request, options)
}
func (a *AuthPersistenceClient) UpdateOne(ctx context.Context, database string, request AuthPredicateUpdate, options AuthPersistenceOptions) (*AuthResult, error) {
	return a.mutate(ctx, database, "updateOne", request.Model, request, options)
}
func (a *AuthPersistenceClient) FindOne(ctx context.Context, database, model, id string) (*AuthResult, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	for field, value := range map[string]string{"model": model, "id": id} {
		if err := requireID(field, value); err != nil {
			return nil, err
		}
	}
	var out AuthResult
	if err := a.c.do(ctx, "GET", path+"/auth/records/"+url.PathEscape(model)+"/"+url.PathEscape(id), nil, nil, &out, "auth.findOne"); err != nil {
		return nil, err
	}
	return &out, nil
}
func (a *AuthPersistenceClient) transaction(ctx context.Context, database, operation, transactionID, key string) (*AuthResult, error) {
	if strings.TrimSpace(transactionID) == "" || len(transactionID) > 128 {
		return nil, &ValidationError{Field: "transaction_id", Message: "must be nonblank and at most 128 bytes"}
	}
	body := struct {
		ID string `json:"transactionId"`
	}{transactionID}
	return a.mutate(ctx, database, "transactions/"+operation, "", body, AuthPersistenceOptions{IdempotencyKey: key})
}
func (a *AuthPersistenceClient) Begin(ctx context.Context, database, transactionID, key string) (*AuthResult, error) {
	return a.transaction(ctx, database, "begin", transactionID, key)
}
func (a *AuthPersistenceClient) Commit(ctx context.Context, database, transactionID, key string) (*AuthResult, error) {
	return a.transaction(ctx, database, "commit", transactionID, key)
}
func (a *AuthPersistenceClient) Rollback(ctx context.Context, database, transactionID, key string) (*AuthResult, error) {
	return a.transaction(ctx, database, "rollback", transactionID, key)
}
