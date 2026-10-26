package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"strconv"
	"strings"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

// Pass this writer unchanged to command helpers; never capture textual output.
type jsonOutput struct {
	io.Writer
	progress io.Writer
	failed   bool
	stream   bool
	terminal bool
}

func progressOutput(out io.Writer) io.Writer {
	if w, ok := out.(*jsonOutput); ok {
		if w.progress != nil {
			return w.progress
		}
		return os.Stderr
	}
	return out
}

func jsonMode(out io.Writer) bool {
	_, ok := out.(*jsonOutput)
	return ok
}

func emitJSON(out io.Writer, value any) error {
	err := json.NewEncoder(out).Encode(value)
	if w, ok := out.(*jsonOutput); ok && err != nil {
		w.failed = true // A partial write cannot be repaired with another JSON value.
	}
	return err
}

// Stream helpers preserve backpressure: each record is written before the next
// is fetched. A complete record certifies clean EOF, not a database snapshot.
func beginJSONStream(out io.Writer) {
	if w, ok := out.(*jsonOutput); ok {
		w.stream = true
	}
}

func emitJSONRecord(out io.Writer, value any) error {
	if !jsonMode(out) {
		return emitJSON(out, value)
	}
	beginJSONStream(out)
	return emitJSON(out, struct {
		Type string `json:"type"`
		Data any    `json:"data"`
	}{"record", value})
}

func finishJSONStream(out io.Writer) error {
	w, ok := out.(*jsonOutput)
	if !ok || !w.stream || w.terminal || w.failed {
		return nil
	}
	w.terminal = true
	return emitJSON(out, struct {
		Type string `json:"type"`
	}{"complete"})
}

type jsonFailure struct{ error }

func (e *jsonFailure) Unwrap() error { return e.error }

func exitStatus(err error) int {
	if err == nil {
		return 0
	}
	if errors.Is(err, errUsage) {
		return 2
	}
	return 1
}

// jsonArguments accepts the output flag anywhere, while preserving flag values
// and operands after -- verbatim. Other flag parsing stays with each command.
func jsonArguments(args []string) (rest []string, enabled bool, err error) {
	for i := 0; i < len(args); i++ {
		a := args[i]
		if a == "--" {
			return append(rest, args[i:]...), enabled, nil
		}
		name, value, hasValue := strings.Cut(strings.TrimPrefix(a, "-"), "=")
		name = strings.TrimPrefix(name, "-")
		if strings.HasPrefix(a, "-") && name == "json" {
			enabled = true
			if hasValue {
				enabled, err = strconv.ParseBool(value)
				if err != nil {
					return rest, true, errUsage
				}
			}
			continue
		}
		rest = append(rest, a)
		if strings.HasPrefix(a, "-") && !hasValue {
			switch name {
			case "project", "org", "scope", "expires", "allow-cidr", "collection", "name", "trigger-collection", "events", "cron", "data", "limit", "home-region", "replica-regions", "placement":
				if i+1 < len(args) {
					i++
					rest = append(rest, args[i])
				}
			}
		}
	}
	return rest, enabled, nil
}

type commandError struct {
	Code      string `json:"code"`
	Message   string `json:"message"`
	Status    int    `json:"status,omitempty"`
	RequestID string `json:"requestId,omitempty"`
}

// Error bodies, headers, URLs, operands and arbitrary error strings can contain
// secrets. Emit only known metadata and fixed messages, never serialize an SDK error.
func emitJSONError(out io.Writer, err error) error {
	detail := commandError{Code: "command_failed", Message: "Command failed; check authentication, permissions and arguments."}
	var api *v1.APIError
	var auth *tsauth.APIError
	var token *tsauth.Error
	switch {
	case errors.Is(err, context.Canceled):
		detail.Code, detail.Message = "interrupted", "Command interrupted; output is incomplete."
	case errors.Is(err, errUsage):
		detail.Code, detail.Message = "invalid_arguments", errUsage.Error()
	case errors.As(err, &api):
		detail.Code, detail.Status, detail.RequestID = string(api.Code), api.Status, safeIdentifier(api.RequestID)
	case errors.As(err, &auth):
		detail.Code, detail.Status = string(auth.Code), auth.Status
	case errors.As(err, &token):
		detail.Code, detail.Status = token.Code, token.Status
	}
	if detail.Status != 0 {
		detail.Message = http.StatusText(detail.Status)
	}
	if safeIdentifier(detail.Code) == "" {
		detail.Code = "command_failed"
	}
	if errors.Is(err, v1.ErrCommitUncertain) {
		detail.Message = "Write outcome unknown; inspect the resource before retrying."
	}
	kind := ""
	if w, ok := out.(*jsonOutput); ok && w.stream {
		kind, w.terminal = "error", true
	}
	return emitJSON(out, struct {
		Type  string       `json:"type,omitempty"`
		Error commandError `json:"error"`
	}{kind, detail})
}

func safeIdentifier(s string) string {
	if len(s) > 128 || strings.ContainsFunc(s, func(r rune) bool {
		return !(r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || r == '_' || r == '-')
	}) {
		return ""
	}
	return s
}
