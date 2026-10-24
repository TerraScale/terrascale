package main

import (
	"bytes"
	"cmp"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"text/tabwriter"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

// functionFlags are the flags of the functions commands, accepted anywhere
// after the command name.
type functionFlags struct {
	name, collection, events, cron, data string
	limit                                int
}

// parse returns the operands left after the command's flags.
func (f *functionFlags) parse(cmd string, args []string) ([]string, error) {
	fs := flag.NewFlagSet(cmd, flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	switch cmd {
	case "functions deploy":
		fs.StringVar(&f.name, "name", "", "")
		fs.StringVar(&f.collection, "trigger-collection", "", "")
		fs.StringVar(&f.events, "events", "", "")
		fs.StringVar(&f.cron, "cron", "", "")
	case "functions invoke":
		fs.StringVar(&f.data, "data", "", "")
	case "functions logs":
		fs.IntVar(&f.limit, "limit", 0, "")
	}
	var operands []string
	for {
		if fs.Parse(args) != nil {
			return nil, errUsage
		}
		if args = fs.Args(); len(args) == 0 {
			break
		}
		operands, args = append(operands, args[0]), args[1:]
	}
	if f.events != "" && f.collection == "" || f.limit < 0 || f.limit > 100 {
		return nil, errUsage
	}
	return operands, nil
}

// functions runs a functions command on db; args follow the database operand.
func functions(ctx context.Context, db *v1.Database, cmd string, f functionFlags, args []string, out io.Writer) error {
	switch cmd {
	case "functions deploy":
		path, name, err := functionSource(args[0])
		if err != nil {
			return err
		}
		source, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		req := v1.FunctionRequest{Source: string(source), Cron: f.cron}
		if f.collection != "" {
			req.Trigger = &v1.FunctionTrigger{Collection: f.collection}
			if f.events != "" {
				req.Trigger.Events = strings.Split(f.events, ",")
			}
		}
		fn, err := db.DeployFunction(ctx, cmp.Or(f.name, name), req)
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "Deployed function %s\n", fn.Name)
		return nil
	case "functions list":
		list, err := db.Functions(ctx)
		if err != nil {
			return err
		}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "NAME\tTRIGGER\tCRON\tUPDATED")
		for _, fn := range list {
			trigger, cron := "none", "none"
			if fn.Trigger != nil {
				trigger = fn.Trigger.Collection + ":" + cmp.Or(strings.Join(fn.Trigger.Events, ","), "upsert,delete")
			}
			if fn.Cron != nil {
				cron = *fn.Cron
			}
			fmt.Fprintf(w, "%s\t%s\t%s\t%s\n", fn.Name, trigger, cron, fn.UpdatedAt.Format(time.RFC3339))
		}
		return w.Flush()
	case "functions invoke":
		var body []byte
		if f.data != "" {
			var err error
			if body, err = jsonOperand(f.data); err != nil {
				return err
			}
		}
		res, err := db.InvokeFunction(ctx, args[0], body)
		// A handler's non-2xx body, unlike a TerraBase error, has no error envelope.
		if api := (*v1.APIError)(nil); errors.As(err, &api) {
			var env struct{ Error json.RawMessage }
			if json.Unmarshal(api.Body, &env) != nil || len(env.Error) == 0 {
				res = api.Body
			}
		}
		if len(res) > 0 {
			out.Write(res)
			if !bytes.HasSuffix(res, []byte("\n")) {
				fmt.Fprintln(out)
			}
		}
		return err
	case "functions logs":
		logs, err := db.FunctionLogs(ctx, args[0], f.limit)
		if err != nil {
			return err
		}
		enc := json.NewEncoder(out)
		for _, l := range logs {
			if err := enc.Encode(l); err != nil {
				return err
			}
		}
		return nil
	case "functions delete":
		if err := db.DeleteFunction(ctx, args[0]); err != nil {
			return err
		}
		fmt.Fprintf(out, "Deleted function %s\n", args[0])
		return nil
	}
	return errUsage
}

// functionSource returns the source file a deploy reads, a dir's index.js,
// and the default function name: the file name without .js, or the dir name.
func functionSource(arg string) (path, name string, err error) {
	abs, err := filepath.Abs(arg)
	if err != nil {
		return "", "", err
	}
	info, err := os.Stat(abs)
	if err != nil {
		return "", "", err
	}
	if info.IsDir() {
		return filepath.Join(abs, "index.js"), filepath.Base(abs), nil
	}
	return abs, strings.TrimSuffix(filepath.Base(abs), ".js"), nil
}
