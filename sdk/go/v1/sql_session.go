package v1

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"

	"github.com/coder/websocket"
)

// SQLSubprotocol is the SQL WebSocket subprotocol.
const SQLSubprotocol = "terrascale.sql.v1"

// ErrSQLSessionClosed is returned by every call after SQLSession.Close.
var ErrSQLSessionClosed = errors.New("terrabase: SQL session is closed")

// SQLSession is a SQL WebSocket session (terrascale.sql.v1). Outside a
// transaction every call runs as its own transaction; Begin opens one that
// lasts at most 5 seconds. It is safe for concurrent use: calls may overlap
// and answers are matched by frame id. Nothing is resent.
//
// A lost committing answer returns ErrCommitUncertain and closes the session;
// every later call returns the same error. Without a committing request, a lost
// open transaction returns ErrTransactionRolledBack until Rollback clears it.
type SQLSession struct {
	d      *Database
	nextID atomic.Int64

	mu        sync.Mutex
	conn      *sqlConn
	closed    bool
	inTx      bool
	lostTx    bool
	uncertain error
}

type sqlConn struct {
	ws      *websocket.Conn
	sending chan struct{}
	mu      sync.Mutex
	pending map[int64]sqlPending
	err     error // set once the socket is gone
}

type sqlPending struct {
	answer     chan sqlAnswer
	committing bool
	control    bool
}

type sqlFrame struct {
	ID         int64       `json:"id"`
	Type       string      `json:"type"`
	Statements []Statement `json:"statements,omitempty"`
}

type sqlAnswer struct {
	ID      *int64   `json:"id"`
	Type    string   `json:"type"`
	Results []Result `json:"results"`
	Error   *struct {
		Code      ErrorCode `json:"code"`
		Message   string    `json:"message"`
		Statement int       `json:"statement"`
		SQLState  string    `json:"sqlstate"`
	} `json:"error"`
	Transaction bool `json:"transaction"`
	err         error
}

// Connect opens a SQL WebSocket session on the database. The caller closes it.
func (d *Database) Connect(ctx context.Context) (*SQLSession, error) {
	s := &SQLSession{d: d}
	conn, err := s.dial(ctx)
	if err != nil {
		return nil, err
	}
	s.conn = conn
	return s, nil
}

// Execute runs one statement; sql.NamedArg values bind by name.
func (s *SQLSession) Execute(ctx context.Context, query string, args ...any) (*Result, error) {
	return s.ExecuteStatement(ctx, statementOf(query, args))
}

func (s *SQLSession) ExecuteStatement(ctx context.Context, statement Statement) (*Result, error) {
	out, err := s.Batch(ctx, []Statement{statement})
	if err != nil {
		return nil, err
	}
	return &out[0], nil
}

// Query runs one statement and returns its rows.
func (s *SQLSession) Query(ctx context.Context, query string, args ...any) ([]Row, error) {
	res, err := s.Execute(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	return res.Rows, nil
}

// Batch runs 1 to 256 statements: as one transaction outside a transaction,
// or inside the open one.
func (s *SQLSession) Batch(ctx context.Context, statements []Statement) ([]Result, error) {
	wire, err := wireStatements(statements)
	if err != nil {
		return nil, err
	}
	answer, err := s.send(ctx, "execute", wire)
	if err != nil {
		return nil, err
	}
	if len(answer.Results) != len(statements) {
		return nil, &SQLProtocolError{}
	}
	return answer.Results, nil
}

// Begin opens a transaction of at most 5 seconds.
func (s *SQLSession) Begin(ctx context.Context) error {
	_, err := s.send(ctx, "begin", nil)
	return err
}

// Commit commits the open transaction. After a failed statement it rolls back
// and returns ErrTransactionRolledBack.
func (s *SQLSession) Commit(ctx context.Context) error {
	_, err := s.send(ctx, "commit", nil)
	return err
}

// Rollback rolls back the open transaction. After a drop it only clears the
// transaction the server already rolled back.
func (s *SQLSession) Rollback(ctx context.Context) error {
	_, err := s.send(ctx, "rollback", nil)
	return err
}

// Transaction begins a transaction, runs fn and commits. When fn returns an
// error it rolls back the transaction, if one is still open, and returns fn's error.
func (s *SQLSession) Transaction(ctx context.Context, fn func(*SQLSession) error) error {
	if err := s.Begin(ctx); err != nil {
		return err
	}
	if err := fn(s); err != nil {
		s.mu.Lock()
		open := s.inTx || s.lostTx
		s.mu.Unlock()
		if open {
			_ = s.Rollback(context.WithoutCancel(ctx))
		}
		return err
	}
	return s.Commit(ctx)
}

// InTransaction reports whether a transaction is open, as of the last answer.
func (s *SQLSession) InTransaction() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.inTx
}

// Close closes the socket normally; the server rolls back an open transaction.
// The session stays closed.
func (s *SQLSession) Close() error {
	s.mu.Lock()
	conn := s.conn
	s.closed = true
	s.mu.Unlock()
	if conn != nil {
		s.fail(conn, ErrSQLSessionClosed)
		_ = conn.ws.Close(websocket.StatusNormalClosure, "")
	}
	return nil
}

func (s *SQLSession) send(ctx context.Context, typ string, statements []Statement) (sqlAnswer, error) {
	id := s.nextID.Add(1)
	frame, err := json.Marshal(sqlFrame{ID: id, Type: typ, Statements: statements})
	if err != nil {
		return sqlAnswer{}, fmt.Errorf("terrabase: encode SQL frame: %w", err)
	}
	if len(frame) > MaxSQLRequestBytes {
		return sqlAnswer{}, fmt.Errorf("terrabase: SQL frame is %d bytes, over %d", len(frame), MaxSQLRequestBytes)
	}
	conn, err := s.connection(ctx, typ)
	if conn == nil {
		return sqlAnswer{}, err
	}
	answer, err := conn.call(ctx, s, id, typ, frame)
	if err != nil {
		return sqlAnswer{}, err
	}
	if e := answer.Error; e != nil {
		return sqlAnswer{}, &SQLError{Code: e.Code, Message: e.Message, Statement: e.Statement, SQLState: e.SQLState}
	}
	return answer, nil
}

// connection returns the open socket, reconnecting after a drop. A nil conn
// with a nil error means the call was answered locally (Rollback after a drop).
func (s *SQLSession) connection(ctx context.Context, typ string) (*sqlConn, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.uncertain != nil {
		return nil, s.uncertain
	}
	if s.closed {
		return nil, ErrSQLSessionClosed
	}
	if s.conn != nil && s.conn.dead() {
		s.lostTx = s.lostTx || s.inTx
		s.conn, s.inTx = nil, false
	}
	if s.lostTx {
		if typ == "rollback" {
			s.lostTx = false
			return nil, nil
		}
		return nil, &SQLError{Code: ErrTransactionRolledBack, Message: "the transaction rolled back when the SQL session's socket dropped", SQLState: "40000"}
	}
	if s.conn == nil {
		conn, err := s.dial(ctx)
		if err != nil {
			return nil, err
		}
		s.conn = conn
	}
	return s.conn, nil
}

func (s *SQLSession) dial(ctx context.Context) (*sqlConn, error) {
	ctx, cancel := context.WithTimeout(ctx, OperationTimeout)
	defer cancel()
	c := s.d.c
	p, err := s.d.path("sql", "ws")
	if err != nil {
		return nil, err
	}
	c, err = c.projectClient(s.d.project)
	if err != nil {
		return nil, err
	}
	target, err := c.url(ctx, p, nil)
	if err != nil {
		return nil, err
	}
	target = "ws" + strings.TrimPrefix(target, "http")
	refreshed := false
	overloads := 1
	for {
		token, err := c.bearerToken(ctx, "")
		if err != nil {
			return nil, err
		}
		header := http.Header{"Authorization": {"Bearer " + token}}
		c.pin(header)
		ws, resp, err := websocket.Dial(ctx, target, &websocket.DialOptions{
			HTTPClient:   c.hc,
			HTTPHeader:   header,
			Subprotocols: []string{SQLSubprotocol},
		})
		if err != nil {
			if resp == nil || resp.StatusCode == http.StatusSwitchingProtocols {
				return nil, fmt.Errorf("terrabase: open SQL session: %w", err)
			}
			body, _ := io.ReadAll(resp.Body)
			apiErr := parseAPIError(resp.StatusCode, resp.Header, body)
			if resp.StatusCode == http.StatusUnauthorized && c.invalidateToken(token, apiErr, refreshed) {
				refreshed = true
				continue
			}
			if retry, waitErr := waitSQLConnect(ctx, apiErr, overloads); waitErr != nil {
				return nil, waitErr
			} else if retry {
				overloads++
				continue
			}
			return nil, apiErr
		}
		if ws.Subprotocol() != SQLSubprotocol {
			_ = ws.Close(websocket.StatusProtocolError, "")
			return nil, fmt.Errorf("terrabase: server did not select the %s subprotocol", SQLSubprotocol)
		}
		ws.SetReadLimit(c.maxResponse)
		conn := &sqlConn{ws: ws, sending: make(chan struct{}, 1), pending: map[int64]sqlPending{}}
		go s.receive(conn)
		return conn, nil
	}
}

func (s *SQLSession) receive(conn *sqlConn) {
	for {
		typ, data, err := conn.ws.Read(context.Background())
		if err == nil && typ != websocket.MessageText {
			err = errors.New("terrabase: SQL session received a binary frame")
		}
		var answer sqlAnswer
		if err == nil && (decodeExact(data, &answer) != nil || (answer.Type != "result" && answer.Type != "error") || (answer.Type == "error") != (answer.Error != nil)) {
			err = &SQLProtocolError{}
		}
		if err == nil && answer.ID == nil && answer.Error != nil {
			err = &SQLError{Code: answer.Error.Code, Message: answer.Error.Message, Statement: answer.Error.Statement, SQLState: answer.Error.SQLState}
		}
		if err != nil {
			s.fail(conn, fmt.Errorf("terrabase: SQL session socket closed: %w", err))
			_ = conn.ws.CloseNow()
			return
		}
		s.mu.Lock()
		conn.mu.Lock()
		if s.conn == conn {
			s.inTx = answer.Transaction
		}
		if answer.ID != nil {
			if pending, ok := conn.pending[*answer.ID]; ok {
				if e := answer.Error; e != nil && (e.Code == ErrWriteOutcomeUnknown || e.Code == ErrCommitUncertain) {
					s.uncertain = &SQLError{Code: e.Code, Message: e.Message, Statement: e.Statement, SQLState: e.SQLState}
				}
				pending.answer <- answer
				delete(conn.pending, *answer.ID)
			}
		}
		uncertain := s.uncertain
		conn.mu.Unlock()
		s.mu.Unlock()
		if uncertain != nil {
			s.fail(conn, uncertain)
			_ = conn.ws.CloseNow()
			return
		}
	}
}

func (c *sqlConn) call(ctx context.Context, s *SQLSession, id int64, typ string, frame []byte) (sqlAnswer, error) {
	if err := ctx.Err(); err != nil {
		return sqlAnswer{}, err
	}
	ch := make(chan sqlAnswer, 1)
	select {
	case <-ctx.Done():
		return sqlAnswer{}, ctx.Err()
	case c.sending <- struct{}{}:
	}
	s.mu.Lock()
	c.mu.Lock()
	if c.err != nil || s.uncertain != nil {
		err := c.err
		if s.uncertain != nil {
			err = s.uncertain
		}
		c.mu.Unlock()
		s.mu.Unlock()
		<-c.sending
		return sqlAnswer{}, err
	}
	committing := typ == "commit" || typ == "execute" && !s.inTx
	if typ == "execute" {
		for _, pending := range c.pending {
			committing = committing || pending.control
		}
	}
	c.pending[id] = sqlPending{answer: ch, committing: committing, control: typ == "begin" || typ == "commit" || typ == "rollback"}
	c.mu.Unlock()
	s.mu.Unlock()
	writeErr := c.ws.Write(ctx, websocket.MessageText, frame)
	<-c.sending
	if err := writeErr; err != nil {
		s.fail(c, fmt.Errorf("terrabase: SQL session socket closed: %w", err))
		_ = c.ws.CloseNow()
	}
	select {
	case answer := <-ch:
		return answer, answer.err
	case <-ctx.Done():
		s.fail(c, ctx.Err())
		_ = c.ws.CloseNow()
		answer := <-ch
		return answer, answer.err
	}
}

func (s *SQLSession) fail(c *sqlConn, err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.err == nil {
		c.err = err
	}
	for _, pending := range c.pending {
		if pending.committing && s.uncertain == nil {
			s.uncertain = fmt.Errorf("%w: %w", ErrCommitUncertain, c.err)
		}
	}
	if s.conn == c {
		s.lostTx = s.uncertain == nil && (s.lostTx || s.inTx)
		s.conn, s.inTx = nil, false
	}
	failure := c.err
	if s.uncertain != nil {
		failure = s.uncertain
	}
	for id, pending := range c.pending {
		pending.answer <- sqlAnswer{err: failure}
		delete(c.pending, id)
	}
}

func (c *sqlConn) dead() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.err != nil
}
