package main

import (
	"context"
	"fmt"
	"io"
	"maps"
	"slices"
	"strings"

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
	valueFlags := []string{"project", "org", "expected-id", "scope", "expires", "allow-cidr", "home-region"}
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
