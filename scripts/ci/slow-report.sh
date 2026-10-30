#!/usr/bin/env bash
set -euo pipefail
suite_completed=true
if [[ ! -f slow/exit || ! -f slow/log ]]; then
  suite_completed=false
  mkdir -p slow
  echo "Slow $AREA suites did not finish building: $RUN_URL" | tee -a "$GITHUB_STEP_SUMMARY" > slow/log
  echo 1 > slow/exit
fi
code=$(<slow/exit)
mapfile -t failed < <(sed -nE 's/^failed (.+) \([^)]*\)$/\1/p' slow/log | sort -u)
{
  echo "## Slow suites: exit $code, ${#failed[@]} failed"
  echo '```'
  grep -E '^Test run summary|^  (total|failed|succeeded|skipped|duration):' slow/log || true
  echo '```'
  [[ ${#failed[@]} -eq 0 ]] || printf -- '- %s\n' "${failed[@]}"
} >> "$GITHUB_STEP_SUMMARY"
if (( code != 0 )); then
  # Issues track main only; a pull request or a dispatched branch reports in the summary.
  [[ $GITHUB_REF == refs/heads/main ]] || exit "$code"
  [[ ${#failed[@]} -gt 0 ]] || failed=("Slow suites failed without a failed test (build or runner error)")
  gh label create slow-failure -R "$GITHUB_REPOSITORY" --color B60205 --description "A Slow test failed on main" --force
  open=$(gh issue list -R "$GITHUB_REPOSITORY" --label slow-failure --state open --limit 500 --json number,title)
  for test in "${failed[@]}"; do
    title="Slow: $test"
    # The exception and stack, without the captured server output; credentials redacted.
    excerpt=$(awk -v t="failed $test " 'index($0, t) == 1 { on = 1; next } on && (/^[^ ]/ || /^  Standard output/ || ++n > 80) { exit } on' slow/log \
      | sed -E 's/(Bearer|Basic) [^ "]+/\1 [redacted]/g; s/((api[_-]?key|token|secret|password)["=: ]+)[^ ",]+/\1[redacted]/Ig')
    # The suite that reported it: its area's make target runs only that suite.
    target=$(awk -v t="failed $test " 'index($0, t) == 1 { on = 1 } on && /^Test run summary/ { print; exit }' slow/log)
    case $target in *TerraBase.Slow*) target="terrabase-test-slow" ;; *Authentication.Slow*) target="auth-test-slow" ;; *) target="test-slow" ;; esac
    body=$(printf '%s\n' \
      "Failed on \`${SOURCE_SHA:-$GITHUB_SHA}\` in the Slow job: $RUN_URL" "" \
      "Environment: \`$GITHUB_JOB ($AREA)\` job on GitHub-hosted runner, Dockerfile stage \`test-slow-$AREA\`, Release build." \
      "Reproduce: \`nice -n 19 make $target TEST_ARGS=\"--treenode-filter '/*/*/*/${test%%(*}'\"\`" \
      "Expected: the test passes. Actual:" "" '```' "${excerpt:-see the run log}" '```')
    number=$(jq -r --arg t "$title" "first(.[] | select(.title == \$t) | .number) // empty" <<< "$open")
    if [[ -n $number ]]; then
      gh issue comment "$number" -R "$GITHUB_REPOSITORY" --body "$body"
    else
      gh issue create -R "$GITHUB_REPOSITORY" --title "$title" --label slow-failure --body "$body"
    fi
  done
fi
# Mark only completed suites after every required issue API call succeeds.
if [[ $suite_completed == true ]]; then
  echo 'completed=true' >> "$GITHUB_OUTPUT"
fi
exit "$code"
