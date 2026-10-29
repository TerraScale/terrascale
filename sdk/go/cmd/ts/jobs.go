package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
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
	var matches []tsauth.Organization
	for o, err := range pages(func(after string) (*v1.Page[tsauth.Organization], error) {
		p, err := u.Organizations(ctx, tsauth.PageOptions{Limit: 100, After: after})
		return (*v1.Page[tsauth.Organization])(p), err
	}) {
		if err != nil {
			return err
		}
		if org == "" || o.Name == org || o.ID == org {
			matches = append(matches, o)
		}
	}
	switch {
	case len(matches) == 0 && org != "":
		return fmt.Errorf("no organization %q: ts projects lists them", org)
	case len(matches) == 0:
		o, err := u.CreateOrganization(ctx, name)
		if err != nil {
			return err
		}
		matches = append(matches, *o)
	case len(matches) > 1:
		return fmt.Errorf("%d organizations match: pick one with -org <id> (ts projects lists them)", len(matches))
	}
	p, err := u.CreateProject(ctx, matches[0].ID, name)
	if err != nil {
		return err
	}
	fmt.Fprintf(out, "Created project %s (%s) in %s\n", p.Name, p.ID, matches[0].Name)
	return nil
}

// tailChanges prints each committed change as a JSON line, starting now.
func tailChanges(ctx context.Context, db *v1.Database, collections []string, out io.Writer) error {
	enc := json.NewEncoder(out)
	for e, err := range db.Changes(ctx, v1.ChangesOptions{Cursor: "now", Collections: collections}) {
		if errors.Is(err, context.Canceled) {
			return nil
		}
		if err != nil {
			return err
		}
		if e.Type != "change" {
			continue
		}
		if err := enc.Encode(e); err != nil {
			return err
		}
	}
	return nil
}

// importPoll is how often import waits for the job's next state.
const importPoll = 2 * time.Second

// importFile creates db, stages the file, prints the preview, confirms it
// and waits until the import finishes.
func importFile(ctx context.Context, db *v1.Database, format, path string, out io.Writer) error {
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
	job, err := db.CreateImport(ctx, v1.CreateImportRequest{Format: format, SourceBytes: size, SourceSHA256: hex.EncodeToString(h.Sum(nil))})
	if err != nil {
		return err
	}
	part := make([]byte, 4<<20)
	for job.Source.ReceivedBytes < size {
		n, err := f.ReadAt(part, job.Source.ReceivedBytes)
		if err != nil && !errors.Is(err, io.EOF) {
			return err
		}
		if job, err = db.UploadImportSource(ctx, job.Source.ReceivedBytes, part[:n]); err != nil {
			return err
		}
	}
	if job, err = waitImport(ctx, db, "awaiting_source", "analyzing"); err != nil {
		return err
	}
	if job.State != "ready" || job.Preview == nil {
		return importFailed(job)
	}
	w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
	fmt.Fprintln(w, "SOURCE\tCOLLECTION\tROWS\tISSUES")
	for _, t := range job.Preview.Tables {
		if !t.Excluded {
			fmt.Fprintf(w, "%s\t%s\t%d\t%d\n", t.Source, t.Collection, t.Rows, len(t.Issues))
		}
	}
	w.Flush()
	if job.Preview.Blocking > 0 {
		return fmt.Errorf("import has %d blocking issues: ts import status %s prints them", job.Preview.Blocking, job.Database)
	}
	if _, err := db.StartImport(ctx, job.Preview.ID); err != nil {
		return err
	}
	if job, err = waitImport(ctx, db, "ready", "queued", "running", "validating"); err != nil {
		return err
	}
	if job.State != "completed" || job.Progress == nil {
		return importFailed(job)
	}
	fmt.Fprintf(out, "Imported %d rows into %s\n", job.Progress.RowsWritten, job.Database)
	return nil
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
	b := new(strings.Builder)
	switch shell {
	case "bash", "zsh":
		if shell == "zsh" {
			b.WriteString("autoload -U +X bashcompinit && bashcompinit\n")
		}
		b.WriteString("_ts() {\n  local words=\"\"\n  case $COMP_CWORD:${COMP_WORDS[1]} in\n")
		fmt.Fprintf(b, "    1:*) words=%q ;;\n", strings.Join(words[""], " "))
		for _, k := range slices.Sorted(maps.Keys(words)) {
			if k != "" {
				fmt.Fprintf(b, "    2:%s) words=%q ;;\n", k, strings.Join(words[k], " "))
			}
		}
		b.WriteString("  esac\n  COMPREPLY=($(compgen -W \"$words\" -- \"${COMP_WORDS[COMP_CWORD]}\"))\n}\ncomplete -F _ts ts\n")
	case "fish":
		fmt.Fprintf(b, "complete -c ts -f -n __fish_use_subcommand -a %q\n", strings.Join(words[""], " "))
		for _, k := range slices.Sorted(maps.Keys(words)) {
			if k != "" {
				fmt.Fprintf(b, "complete -c ts -f -n '__fish_seen_subcommand_from %s; and test (count (commandline -opc)) -eq 2' -a %q\n",
					k, strings.Join(words[k], " "))
			}
		}
	default:
		return errUsage
	}
	_, err := io.WriteString(out, b.String())
	return err
}
