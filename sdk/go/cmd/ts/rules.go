package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"slices"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

const rulesFile = "terrascale.rules.json"
const rulesStateFile = ".terrascale.rules.state.json"

type rulesFlags struct {
	operation, table, auth, row, new string
	draft                            bool
}

func (f *rulesFlags) parse(command string, args []string) ([]string, error) {
	fs := flag.NewFlagSet(command, flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	if command == "rules test" {
		fs.StringVar(&f.operation, "operation", "", "")
		fs.StringVar(&f.table, "table", "", "")
		fs.StringVar(&f.auth, "auth", "", "")
		fs.StringVar(&f.row, "row", "{}", "")
		fs.StringVar(&f.new, "new", "{}", "")
		fs.BoolVar(&f.draft, "draft", false, "")
	}
	var operands []string
	for {
		if err := fs.Parse(args); err != nil {
			return nil, parserError(err)
		}
		args = fs.Args()
		if len(args) == 0 {
			break
		}
		operands, args = append(operands, args[0]), args[1:]
	}
	if command == "rules test" && (!slices.Contains([]string{"read", "create", "update", "delete"}, f.operation) || f.table == "" || f.auth == "") {
		return nil, fmt.Errorf("rules test requires --operation read|create|update|delete, --table and --auth: %w", errUsage)
	}
	return operands, nil
}

type rulesState struct {
	Project  string  `json:"project"`
	Database string  `json:"database"`
	Version  *string `json:"version"`
}

func runRules(ctx context.Context, db *v1.Database, command string, flags rulesFlags, input commandInput, out io.Writer) error {
	if command == "rules test" {
		return simulateRules(ctx, db, flags, out)
	}
	current, err := db.GetRules(ctx)
	if err != nil && !errors.Is(err, v1.ErrorCode("no_rules")) {
		return err
	}
	live := v1.RulesSet{Tables: map[string]v1.RulesTable{}}
	state := rulesState{Project: db.Project(), Database: db.Name()}
	if err == nil {
		live.Tables = current.Tables
		state.Version = &current.Version
	}
	if command == "rules pull" {
		body, err := prettyRules(live)
		if err != nil {
			return err
		}
		if err := writeRulesFile(rulesFile, body); err != nil {
			return err
		}
		if err := saveRulesState(state); err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				Path    string      `json:"path"`
				Version *string     `json:"version"`
				Rules   v1.RulesSet `json:"rules"`
			}{rulesFile, state.Version, live})
		}
		_, err = fmt.Fprintf(out, "Pulled %s (version %s)\n", rulesFile, rulesVersion(state.Version))
		return err
	}
	desired, err := readRulesFile()
	if err != nil {
		return err
	}
	before, err := prettyRules(live)
	if err != nil {
		return err
	}
	after, err := prettyRules(desired)
	if err != nil {
		return err
	}
	if err := printRulesDiff(progressOutput(out), string(before), string(after)); err != nil {
		return err
	}
	saved, err := os.ReadFile(rulesStateFile)
	if err == nil {
		var pulled rulesState
		if err := json.Unmarshal(saved, &pulled); err != nil {
			return fmt.Errorf("read rules version state: %w", err)
		}
		if pulled.Project != state.Project || pulled.Database != state.Database {
			return errors.New("rules version state belongs to another database; run ts rules pull for this database first")
		}
		if rulesVersion(pulled.Version) != rulesVersion(state.Version) {
			return rulesChanged(nil)
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	message := fmt.Sprintf("Deploy rules to database %q in project %s.", db.Name(), db.Project())
	if err := confirmNamedOperation(db.Name(), message, command, input.yes, input.in, input.prompt, input.tty); err != nil {
		return err
	}
	deployed, err := db.DeployRules(ctx, *desired, state.Version)
	if err != nil {
		var api *v1.APIError
		if errors.As(err, &api) && api.Status == 412 {
			return rulesChanged(err)
		}
		if errors.As(err, &api) && errors.Is(err, v1.ErrInvalidRules) {
			var body struct {
				Error struct {
					Details struct {
						Problems []v1.RulesProblem `json:"problems"`
					} `json:"details"`
				} `json:"error"`
			}
			if decodeErr := json.Unmarshal(api.Body, &body); decodeErr != nil {
				return errors.Join(err, decodeErr)
			}
			if !jsonMode(out) {
				if printErr := printRulesProblems(out, body.Error.Details.Problems); printErr != nil {
					return printErr
				}
			}
			return &cliError{code: "invalid_rules", message: "Rules deployment failed; fix the listed problems.", status: api.Status, err: err, problems: body.Error.Details.Problems}
		}
		return err
	}
	state.Version = &deployed.Version
	if err := saveRulesState(state); err != nil {
		return fmt.Errorf("rules deployed, but saving the version failed; pull before deploying again: %w", err)
	}
	if jsonMode(out) {
		return emitJSON(out, deployed)
	}
	if err := printRulesProblems(out, deployed.Warnings); err != nil {
		return err
	}
	_, err = fmt.Fprintln(out, "Deployed rules version", deployed.Version)
	return err
}

func rulesChanged(err error) error {
	return &cliError{code: "version_mismatch", message: "Rules changed since pull; run ts rules pull and reapply your changes before deploying.", status: 412, err: err}
}

func rulesVersion(version *string) string {
	if version == nil {
		return "none"
	}
	return *version
}

func readRulesFile() (*v1.RulesSet, error) {
	f, err := os.Open(rulesFile)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	body, err := io.ReadAll(io.LimitReader(f, 256*1024+1))
	if err != nil {
		return nil, err
	}
	if len(body) > 256*1024 {
		return nil, errors.New("terrascale.rules.json exceeds 256 KiB")
	}
	var rules v1.RulesSet
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&rules); err != nil {
		return nil, fmt.Errorf("read %s: %w", rulesFile, err)
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return nil, errors.New("terrascale.rules.json must contain one JSON object")
	}
	if rules.Tables == nil {
		return nil, errors.New("terrascale.rules.json requires a tables object")
	}
	return &rules, nil
}

func prettyRules(rules any) ([]byte, error) {
	body, err := json.Marshal(rules)
	if err != nil {
		return nil, err
	}
	var ordered any
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.UseNumber()
	if err := decoder.Decode(&ordered); err != nil {
		return nil, err
	}
	body, err = json.MarshalIndent(ordered, "", "  ")
	return append(body, '\n'), err
}

func writeRulesFile(path string, body []byte) error {
	f, err := os.CreateTemp(".", ".terrascale-rules-*")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	_, writeErr := f.Write(body)
	if err := errors.Join(writeErr, f.Close()); err != nil {
		return err
	}
	return os.Rename(f.Name(), path)
}

func saveRulesState(state rulesState) error {
	body, err := prettyRules(state)
	if err != nil {
		return err
	}
	return writeRulesFile(rulesStateFile, body)
}

func printRulesDiff(out io.Writer, before, after string) error {
	if before == after {
		_, err := fmt.Fprintln(out, "Rules are unchanged.")
		return err
	}
	oldLines := strings.Split(strings.TrimSuffix(before, "\n"), "\n")
	newLines := strings.Split(strings.TrimSuffix(after, "\n"), "\n")
	start := 0
	for start < min(len(oldLines), len(newLines)) && oldLines[start] == newLines[start] {
		start++
	}
	oldEnd, newEnd := len(oldLines), len(newLines)
	for oldEnd > start && newEnd > start && oldLines[oldEnd-1] == newLines[newEnd-1] {
		oldEnd--
		newEnd--
	}
	var diff strings.Builder
	fmt.Fprintf(&diff, "--- deployed\n+++ %s\n@@ -%d,%d +%d,%d @@\n", rulesFile, start+1, oldEnd-start, start+1, newEnd-start)
	for _, line := range oldLines[start:oldEnd] {
		fmt.Fprintln(&diff, "-"+line)
	}
	for _, line := range newLines[start:newEnd] {
		fmt.Fprintln(&diff, "+"+line)
	}
	_, err := io.WriteString(out, diff.String())
	return err
}

func printRulesProblems(out io.Writer, problems []v1.RulesProblem) error {
	for _, problem := range problems {
		if _, err := fmt.Fprintf(out, "%s/%s: %s: %s\n", problem.Table, problem.Field, problem.Code, problem.Message); err != nil {
			return err
		}
	}
	return nil
}

func simulateRules(ctx context.Context, db *v1.Database, flags rulesFlags, out io.Writer) error {
	request := v1.RulesTestRequest{Operation: flags.operation, Table: flags.table}
	for _, field := range []struct {
		name, source string
		target       any
	}{
		{"auth", flags.auth, &request.Auth},
		{"row", flags.row, &request.Row},
		{"new", flags.new, &request.New},
	} {
		if !strings.HasPrefix(strings.TrimSpace(field.source), "{") {
			return fmt.Errorf("--%s must be a JSON object: %w", field.name, errUsage)
		}
		decoder := json.NewDecoder(strings.NewReader(field.source))
		decoder.UseNumber()
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(field.target); err != nil {
			return fmt.Errorf("--%s must be a JSON object: %w", field.name, errUsage)
		}
		if err := decoder.Decode(new(any)); err != io.EOF {
			return fmt.Errorf("--%s must contain one JSON object: %w", field.name, errUsage)
		}
	}
	if flags.draft {
		var err error
		request.Rules, err = readRulesFile()
		if err != nil {
			return err
		}
	}
	result, err := db.TestRules(ctx, request)
	if err != nil {
		return err
	}
	if jsonMode(out) {
		if err := emitJSON(out, result); err != nil {
			return err
		}
	} else {
		clause := "null"
		if result.Clause != nil {
			clause = *result.Clause
		}
		if _, err := fmt.Fprintf(out, "allowed: %t\ndecidedBy: %s\nclause: %s\ncolumns: %s\n", result.Allowed, result.DecidedBy, clause, strings.Join(result.Columns, ", ")); err != nil {
			return err
		}
		if err := printRulesProblems(out, result.Problems); err != nil {
			return err
		}
	}
	if !result.Allowed {
		err := &cliError{code: "rules_denied", message: "Rules denied the simulated request."}
		if jsonMode(out) {
			return &jsonFailure{err}
		}
		return err
	}
	return nil
}
