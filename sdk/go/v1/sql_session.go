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
	"terrascale.tech/sdk/internal/causal"
)

const SQLSubprotocol = "terrascale.sql.v2"

var ErrSQLSessionClosed = errors.New("terrabase: SQL session is closed")

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
	err     error
}

type sqlPending struct {
	answer     chan sqlAnswer
	committing bool
	control    bool
}

type sqlFrame struct {
	ID          int64       `json:"id"`
	Type        string      `json:"type"`
	Statements  []Statement `json:"statements,omitempty"`
	Consistency Consistency `json:"consistency,omitempty"`
	Bookmark    string      `json:"bookmark,omitempty"`
}

type sqlAnswer struct {
	ID          int64           `json:"id"`
	Type        string          `json:"type"`
	Results     []Result        `json:"results"`
	Bookmark    string          `json:"bookmark"`
	Error       *sqlSocketError `json:"error"`
	Transaction *bool           `json:"transaction"`
	err         error
}

type sqlSocketError struct {
	Code      ErrorCode `json:"code"`
	Message   string    `json:"message"`
	Statement int       `json:"statement"`
	SQLState  string    `json:"sqlState"`
}

func (e *sqlSocketError) refusal() *SQLError {
	return &SQLError{Code: e.Code, Message: e.Message, Statement: e.Statement, SQLState: e.SQLState}
}

func (d *Database) Connect(ctx context.Context) (*SQLSession, error) {
	s := &SQLSession{d: d}
	if _, err := s.connection(ctx, ""); err != nil {
		return nil, err
	}
	return s, nil
}

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

func (s *SQLSession) Query(ctx context.Context, query string, args ...any) ([]Row, error) {
	res, err := s.Execute(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	return res.Rows, nil
}

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

func (s *SQLSession) Begin(ctx context.Context) error {
	_, err := s.send(ctx, "begin", nil)
	return err
}

func (s *SQLSession) Commit(ctx context.Context) error {
	_, err := s.send(ctx, "commit", nil)
	return err
}

func (s *SQLSession) Rollback(ctx context.Context) error {
	_, err := s.send(ctx, "rollback", nil)
	return err
}

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

func (s *SQLSession) InTransaction() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.inTx
}

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
	bookmark, err := s.d.causal.Outgoing(ctx)
	if err != nil {
		return sqlAnswer{}, err
	}
	mode, err := causal.ConsistencyFrom(ctx)
	if err != nil {
		return sqlAnswer{}, err
	}
	if typ == "commit" || typ == "rollback" {
		mode = ""
	}
	id := s.nextID.Add(1)
	frame, err := json.Marshal(sqlFrame{ID: id, Type: typ, Statements: statements, Consistency: mode, Bookmark: bookmark})
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
		return sqlAnswer{}, e.refusal()
	}
	return answer, nil
}

func (s *SQLSession) connection(ctx context.Context, typ string) (*sqlConn, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.uncertain != nil {
		return nil, s.uncertain
	}
	if s.closed {
		return nil, ErrSQLSessionClosed
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
		header, err := causalHeader(ctx, s.d.causal)
		if err != nil {
			return nil, err
		}
		header.Set("Authorization", "Bearer "+token)
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
		ws.SetReadLimit(DefaultMaxResponseBytes)
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
		if err == nil && (decodeExact(data, &answer) != nil || answer.Transaction == nil ||
			(answer.Type != "result" && answer.Type != "error") || (answer.Type == "error") != (answer.Error != nil) ||
			s.d.causal.Observe(answer.Bookmark) != nil) {
			err = &SQLProtocolError{}
		}
		if err == nil && answer.ID == 0 && answer.Error != nil {
			err = answer.Error.refusal()
		}
		if err != nil {
			s.fail(conn, fmt.Errorf("terrabase: SQL session socket closed: %w", err))
			_ = conn.ws.CloseNow()
			return
		}
		s.mu.Lock()
		conn.mu.Lock()
		if s.conn == conn {
			s.inTx = *answer.Transaction
		}
		if answer.ID != 0 {
			if pending, ok := conn.pending[answer.ID]; ok {
				if e := answer.Error; e != nil && (e.Code == ErrWriteOutcomeUnknown || e.Code == ErrCommitUncertain) {
					s.uncertain = e.refusal()
				}
				pending.answer <- answer
				delete(conn.pending, answer.ID)
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
	for _, pending := range c.pending {
		pending.answer <- sqlAnswer{err: failure}
	}
	clear(c.pending)
}
