// Command ts is the TerraScale CLI: device sign-in and database commands
// against the production API, as the signed-in user.
//
//	ts auth login | logout
//	ts db list | create <name> | delete <name> | branch <source> <name>
//	ts sql <db> <query>
//
// Database commands act in the user's only project; -project <name or id>
// picks one when the user reaches several.
package main

import (
	"cmp"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"iter"
	"os"
	"os/signal"
	"path/filepath"
	"text/tabwriter"
	"time"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

var version = "dev" // Set by release builds with -ldflags.

const usage = `usage:
  ts version | --version
  ts auth login | logout
  ts [-project <name or id>] db list | create <name> | delete <name> | branch <source> <name>
  ts [-project <name or id>] sql <db> <query>
`

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	if err := run(ctx, os.Args[1:], os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, "ts:", err)
		os.Exit(1)
	}
}

var errUsage = errors.New("invalid arguments\n" + usage)

func run(ctx context.Context, args []string, out io.Writer) error {
	if len(args) == 1 && (args[0] == "version" || args[0] == "--version") {
		_, err := fmt.Fprintln(out, "ts", version)
		return err
	}
	fs := flag.NewFlagSet("ts", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	project := fs.String("project", "", "project name or id")
	if fs.Parse(args) != nil {
		return errUsage
	}
	args = fs.Args()
	device, err := tsauth.NewDevice(tsauth.DeviceConfig{})
	if err != nil {
		return err
	}
	switch {
	case len(args) == 2 && args[0] == "auth" && args[1] == "login":
		return login(ctx, device, out)
	case len(args) == 2 && args[0] == "auth" && args[1] == "logout":
		return logout(ctx, device, out)
	case len(args) >= 2 && (args[0] == "db" || args[0] == "sql"):
		s, err := open(ctx, device, *project)
		if err != nil {
			return err
		}
		return s.run(ctx, args, out)
	}
	return errUsage
}

// credentials are the CLI's sign-in, kept in the user's config directory.
type credentials struct {
	AccessToken  string    `json:"accessToken"`
	RefreshToken string    `json:"refreshToken"`
	Expiry       time.Time `json:"expiry"`
}

func credentialsPath() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "terrascale", "cli.json"), nil
}

func save(t *tsauth.Tokens) error {
	path, err := credentialsPath()
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	data, err := json.Marshal(credentials{t.AccessToken, t.RefreshToken, t.Expiry})
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0o600)
}

func login(ctx context.Context, d *tsauth.Device, out io.Writer) error {
	a, err := d.Start(ctx)
	if err != nil {
		return err
	}
	fmt.Fprintf(out, "Open %s\nand confirm the code %s\n", a.VerificationURIComplete, a.UserCode)
	t, err := d.Wait(ctx, a)
	if err != nil {
		return err
	}
	if err := save(t); err != nil {
		return err
	}
	fmt.Fprintln(out, "Signed in.")
	return nil
}

func logout(ctx context.Context, d *tsauth.Device, out io.Writer) error {
	path, err := credentialsPath()
	if err != nil {
		return err
	}
	var c credentials
	if data, err := os.ReadFile(path); err == nil && json.Unmarshal(data, &c) == nil && c.RefreshToken != "" {
		if err := d.Revoke(ctx, c.RefreshToken); err != nil {
			return err
		}
	}
	if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	fmt.Fprintln(out, "Signed out.")
	return nil
}

// accessToken returns a current access token, refreshing the stored sign-in
// when it expires within a minute.
func accessToken(ctx context.Context, d *tsauth.Device) (string, error) {
	path, err := credentialsPath()
	if err != nil {
		return "", err
	}
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return "", errors.New("not signed in: run ts auth login")
	}
	var c credentials
	if err != nil || json.Unmarshal(data, &c) != nil {
		return "", fmt.Errorf("unreadable %s: run ts auth login", path)
	}
	if time.Until(c.Expiry) > time.Minute {
		return c.AccessToken, nil
	}
	t, err := d.Refresh(ctx, c.RefreshToken)
	if err != nil {
		return "", fmt.Errorf("sign-in expired, run ts auth login: %w", err)
	}
	t.RefreshToken = cmp.Or(t.RefreshToken, c.RefreshToken) // ts-auth may keep the old one
	return t.AccessToken, save(t)
}

// session is the signed-in user's TerraBase client for one project.
type session struct {
	tb      *v1.Client
	project string
}

func open(ctx context.Context, d *tsauth.Device, project string) (*session, error) {
	token, err := accessToken(ctx, d)
	if err != nil {
		return nil, err
	}
	auth, err := tsauth.New(tsauth.Config{})
	if err != nil {
		return nil, err
	}
	u := auth.WithToken(token)
	var matches []tsauth.Project
	for org, err := range pages(func(after string) (*v1.Page[tsauth.Organization], error) {
		p, err := u.Organizations(ctx, tsauth.PageOptions{Limit: 100, After: after})
		return (*v1.Page[tsauth.Organization])(p), err
	}) {
		if err != nil {
			return nil, err
		}
		for p, err := range pages(func(after string) (*v1.Page[tsauth.Project], error) {
			p, err := u.Projects(ctx, org.ID, tsauth.PageOptions{Limit: 100, After: after})
			return (*v1.Page[tsauth.Project])(p), err
		}) {
			if err != nil {
				return nil, err
			}
			if project == "" || p.Name == project || p.ID == project {
				matches = append(matches, p)
			}
		}
	}
	switch {
	case len(matches) == 0 && project != "":
		return nil, fmt.Errorf("no project %q", project)
	case len(matches) == 0:
		return nil, errors.New("no project: create one at https://dash.terrascale.tech")
	case len(matches) > 1:
		return nil, fmt.Errorf("%d projects match: pick one with -project <id>", len(matches))
	}
	p := matches[0]
	// Empty Scopes: ts-auth grants what the user's project role allows.
	tokens, err := tsauth.NewExchangeSource(tsauth.ExchangeConfig{ClientID: tsauth.CLIClientID,
		SubjectToken: func(context.Context) (string, error) { return token, nil }, Organization: p.OrganizationID, Project: p.ID})
	if err != nil {
		return nil, err
	}
	tb, err := v1.New(v1.Config{TokenSource: tokens})
	if err != nil {
		return nil, err
	}
	return &session{tb, p.ID}, nil
}

// pages yields every item of a paged list; list gets the previous Next.
func pages[T any](list func(after string) (*v1.Page[T], error)) iter.Seq2[T, error] {
	return func(yield func(T, error) bool) {
		after := ""
		for {
			p, err := list(after)
			if err != nil {
				var zero T
				yield(zero, err)
				return
			}
			for _, item := range p.Items {
				if !yield(item, nil) {
					return
				}
			}
			if after = p.Next; after == "" {
				return
			}
		}
	}
}

func (s *session) run(ctx context.Context, args []string, out io.Writer) error {
	switch {
	case args[0] == "sql" && len(args) == 3:
		res, err := s.tb.Database(s.project, args[1]).SQL(ctx, args[2])
		if err != nil {
			return err
		}
		for _, row := range res.Rows {
			fmt.Fprintf(out, "%s\n", row.Value)
		}
		if res.Truncated {
			fmt.Fprintf(os.Stderr, "ts: truncated: %d rows matched\n", res.Matched)
		}
		return nil
	case args[1] == "list" && len(args) == 2:
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "NAME\tID\tHOME REGION")
		for db, err := range pages(func(after string) (*v1.Page[v1.ManagedDatabase], error) {
			return s.tb.Databases(ctx, s.project, v1.PageOptions{Limit: 100, After: after})
		}) {
			if err != nil {
				return err
			}
			home := ""
			if db.HomeRegion != nil {
				home = *db.HomeRegion
			}
			fmt.Fprintf(w, "%s\t%s\t%s\n", db.Name, db.ID, home)
		}
		return w.Flush()
	case args[1] == "create" && len(args) == 3:
		db, err := s.tb.Database(s.project, args[2]).Create(ctx)
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "Created %s (%s)\n", args[2], db.ID)
		return nil
	case args[1] == "delete" && len(args) == 3:
		db := s.tb.Database(s.project, args[2])
		// TerraBase deletes only unreplicated databases, and new ones replicate.
		if _, err := db.SetRegions(ctx, []string{}); err != nil {
			return err
		}
		if err := db.Delete(ctx); err != nil {
			return err
		}
		fmt.Fprintf(out, "Deleted %s\n", args[2])
		return nil
	case args[1] == "branch" && len(args) == 4:
		db, err := s.tb.Database(s.project, args[2]).Branch(ctx, args[3])
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "Branched %s into %s (%s)\n", args[2], db.Name, db.ID)
		return nil
	}
	return errUsage
}
