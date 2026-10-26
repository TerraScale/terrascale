package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"maps"
	"os"
	"slices"
	"strings"
	"text/tabwriter"
	"time"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

// createProject creates a project in the organization org names, the user's
// only organization, or a new organization of the same name when they have
// none.
func createProject(ctx context.Context, u *tsauth.User, org, name string, out io.Writer) error {
	if org != "" {
		selected, err := selectOrganization(ctx, u, org)
		if err != nil {
			return err
		}
		org = selected.ID
	}
	var matches []tsauth.Organization
	for o, err := range pages(func(after string) (*v1.Page[tsauth.Organization], error) {
		p, err := u.Organizations(ctx, tsauth.PageOptions{Limit: 100, After: after})
		return (*v1.Page[tsauth.Organization])(p), err
	}) {
		if err != nil {
			return err
		}
		if org == "" || o.ID == org {
			matches = append(matches, o)
		}
	}
	switch {
	case len(matches) == 0 && org != "":
		return fmt.Errorf("no organization %q: ts orgs lists them", org)
	case len(matches) == 0:
		o, err := u.CreateOrganization(ctx, name)
		if err != nil {
			return err
		}
		matches = append(matches, *o)
	case len(matches) > 1:
		return fmt.Errorf("%d organizations match: pick one with -org <id> (ts orgs lists them)", len(matches))
	}
	p, err := u.CreateProject(ctx, matches[0].ID, name)
	if err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, p)
	}
	_, err = fmt.Fprintf(out, "Created project %s (%s) in %s\n", p.Name, p.ID, matches[0].Name)
	return err
}

// importPoll is how often import waits for the job's next state.
const importPoll = 2 * time.Second

// importFile creates db and stages the file before preview-bound consent. Only
// StartImport confirms copying documents; already confirmed jobs reattach.
func importFile(ctx context.Context, db *v1.Database, format, path, collection string, confirm func(*v1.ImportJob) error, out io.Writer) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	h := sha256.New()
	size, err := io.Copy(h, f)
	if err != nil {
		return err
	}
	// A rerun resumes: TerraBase answers an identical import request with the same job.
	if _, err := db.Create(ctx); err != nil && !errors.Is(err, v1.ErrNameTaken) {
		return err
	}
	req := v1.CreateImportRequest{Format: format, SourceBytes: size, SourceSHA256: hex.EncodeToString(h.Sum(nil))}
	if collection != "" {
		req.Options = &v1.ImportOptions{Collection: collection}
	}
	job, err := db.CreateImport(ctx, req)
	if err != nil {
		return err
	}
	// Completed jobs release their source; active jobs already confirmed it.
	part := make([]byte, 4<<20)
	for slices.Contains([]string{"awaiting_source", "failed", "cancelled"}, job.State) && job.Source.ReceivedBytes < size {
		n, err := f.ReadAt(part, job.Source.ReceivedBytes)
		if err != nil && !errors.Is(err, io.EOF) {
			return err
		}
		if job, err = db.UploadImportSource(ctx, job.Source.ReceivedBytes, part[:n]); err != nil {
			return err
		}
	}
	if job.State == "failed" || job.State == "cancelled" {
		if job, err = db.ResumeImport(ctx); err != nil {
			return err
		}
	}
	if job.State == "awaiting_source" || job.State == "analyzing" {
		if job, err = waitImport(ctx, db, "awaiting_source", "analyzing"); err != nil {
			return err
		}
	}
	if job.State == "ready" {
		if job.Preview == nil {
			return importFailed(job)
		}
		w := tabwriter.NewWriter(progressOutput(out), 0, 0, 2, ' ', 0)
		if _, err := fmt.Fprintln(w, "SOURCE\tCOLLECTION\tROWS\tISSUES"); err != nil {
			return err
		}
		for _, t := range job.Preview.Tables {
			if !t.Excluded {
				if _, err := fmt.Fprintf(w, "%s\t%s\t%d\t%d\n", t.Source, t.Collection, t.Rows, len(t.Issues)); err != nil {
					return err
				}
			}
		}
		if err := w.Flush(); err != nil {
			return err
		}
		if job.Preview.Blocking > 0 {
			return fmt.Errorf("import has %d blocking issues: ts import status %s prints them", job.Preview.Blocking, job.Database)
		}
		previewID := job.Preview.ID
		if confirm == nil {
			return errors.New("import requires explicit preview confirmation")
		}
		if err := confirm(job); err != nil {
			return err
		}
		if _, err := db.StartImport(ctx, previewID); err != nil {
			return err
		}
	}
	if slices.Contains([]string{"ready", "queued", "running", "validating"}, job.State) {
		if job, err = waitImport(ctx, db, "ready", "queued", "running", "validating"); err != nil {
			return err
		}
	}
	if job.State != "completed" || job.Progress == nil {
		return importFailed(job)
	}
	if jsonMode(out) {
		return emitJSON(out, job)
	}
	_, err = fmt.Fprintf(out, "Imported %d rows into %s\n", job.Progress.RowsWritten, job.Database)
	return err
}

// waitImport reads the import job until its state leaves pending.
func waitImport(ctx context.Context, db *v1.Database, pending ...string) (*v1.ImportJob, error) {
	for {
		job, err := db.Import(ctx)
		if err != nil || !slices.Contains(pending, job.State) {
			return job, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(importPoll):
		}
	}
}

func importFailed(job *v1.ImportJob) error {
	if job.Error != nil {
		return fmt.Errorf("import %s: %s: %s", job.State, job.Error.Code, job.Error.Message)
	}
	return fmt.Errorf("import %s", job.State)
}

// completion prints a completion script that offers each command's words.
func completion(shell string, out io.Writer) error {
	words := map[string][]string{"": {"help", "version"}}
	for name := range commands {
		first, second, _ := strings.Cut(name, " ")
		words[""] = append(words[""], first)
		if second != "" {
			words[first] = append(words[first], second)
		}
	}
	for k, v := range words {
		slices.Sort(v)
		words[k] = slices.Compact(v)
	}
	words["completion"] = []string{"bash", "fish", "zsh"}
	words["backup destination"] = []string{"get", "set", "delete"}
	valueFlags := []string{"project", "org", "scope", "expires", "allow-cidr", "collection", "home-region", "replica-regions", "placement", "cursor"}
	var valuePatterns []string
	flags := "-h -help --help -yes --yes"
	for _, flag := range valueFlags {
		valuePatterns = append(valuePatterns, "-"+flag, "--"+flag)
		flags += " -" + flag + " --" + flag
	}
	b := new(strings.Builder)
	switch shell {
	case "bash", "zsh":
		if shell == "zsh" {
			b.WriteString(`if (( ! $+functions[compdef] )); then
  autoload -Uz compinit && compinit
fi
if (( ! $+functions[complete] )); then
  autoload -Uz bashcompinit && bashcompinit
fi
`)
		}
		b.WriteString("_ts() {\n")
		if shell == "zsh" {
			b.WriteString("  emulate -L sh\n")
		}
		b.WriteString(`  local words="" cmd="" depth=0 pending=0 token i
  COMPREPLY=()
  for ((i=1; i<COMP_CWORD; i++)); do
    token=${COMP_WORDS[i]}
    if (( pending )); then
      [[ $token == = ]] && continue
      pending=0
      continue
    fi
    case $token in
`)
		fmt.Fprintf(b, "      %s) pending=1 ;;\n", strings.Join(valuePatterns, "|"))
		b.WriteString(`      -*) ;;
      *) if (( depth == 0 )); then cmd=$token; elif (( depth == 1 )); then cmd="$cmd $token"; fi; ((depth+=1)) ;;
    esac
  done
  (( pending )) && return 0
  case ${COMP_WORDS[COMP_CWORD]} in
`)
		fmt.Fprintf(b, "    -*) words=%q ;;\n", flags)
		b.WriteString("    *) case $depth:$cmd in\n")
		fmt.Fprintf(b, "      0:*) words=%q ;;\n", strings.Join(words[""], " "))
		for _, k := range slices.Sorted(maps.Keys(words)) {
			if k != "" {
				fmt.Fprintf(b, "      %q) words=%q ;;\n", fmt.Sprintf("%d:%s", len(strings.Fields(k)), k), strings.Join(words[k], " "))
			}
		}
		b.WriteString("    esac ;;\n  esac\n  for token in $words; do\n    [[ $token == \"${COMP_WORDS[COMP_CWORD]}\"* ]] && COMPREPLY+=(\"$token\")\n  done\n  return 0\n}\ncomplete -F _ts ts\n")
	case "fish":
		b.WriteString(`function __ts_words
  set -l args (commandline -opc)
  set -e args[1]
  set -l cmd ''
  set -l depth 0
  set -l pending 0
  for token in $args
    if test $pending -eq 1
      set pending 0
      continue
    end
    switch $token
`)
		fmt.Fprintf(b, "      case %s\n        set pending 1\n", strings.Join(valuePatterns, " "))
		b.WriteString(`      case '-*'
        continue
      case '*'
        if test $depth -eq 0
          set cmd $token
        else if test $depth -eq 1
          set cmd "$cmd $token"
        end
        set depth (math $depth + 1)
    end
  end
  test $pending -eq 0; or return
  switch "$depth:$cmd"
`)
		fmt.Fprintf(b, "    case '0:*'\n      printf '%%s\\n' %s\n", strings.Join(words[""], " "))
		for _, k := range slices.Sorted(maps.Keys(words)) {
			if k != "" {
				fmt.Fprintf(b, "    case '%d:%s'\n      printf '%%s\\n' %s\n", len(strings.Fields(k)), k, strings.Join(words[k], " "))
			}
		}
		b.WriteString("  end\nend\ncomplete -c ts -f -a '(__ts_words)'\n")
		for _, flag := range valueFlags {
			fmt.Fprintf(b, "complete -c ts -o %s -l %s -x\n", flag, flag)
		}
		b.WriteString("complete -c ts -o yes -l yes\ncomplete -c ts -s h -o help -l help\n")
	default:
		return errUsage
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			Shell  string `json:"shell"`
			Script string `json:"script"`
		}{shell, b.String()})
	}
	_, err := io.WriteString(out, b.String())
	return err
}
