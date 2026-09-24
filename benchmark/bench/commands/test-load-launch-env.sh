#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly TEST_SCRIPT_DIR="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
readonly TEST_WORK_DIR="$(mktemp -d /tmp/load-launch-env-test.XXXXXX)"
trap 'rm -rf -- "$TEST_WORK_DIR"' EXIT

source "$TEST_SCRIPT_DIR/load-launch-env.sh"
readonly MUST_NOT_EXIST="$TEST_WORK_DIR/parser-valid-command-substitution"
readonly EXECUTED_MARKER="$TEST_WORK_DIR/parser-malicious-command-substitution"

write_valid_fixture() {
    {
        printf 'SAFE=%q\n' 'ordinary-value'
        printf 'VALUE=%q\n' "literal value; \$(touch $MUST_NOT_EXIST)"
    } > "$1"
}

valid="$TEST_WORK_DIR/valid.env"
write_valid_fixture "$valid"
load_launch_env_file "$valid" SAFE VALUE
[[ "$SAFE" == ordinary-value ]]
[[ "$VALUE" == "literal value; \$(touch $MUST_NOT_EXIST)" ]]
[[ ! -e "$MUST_NOT_EXIST" ]]

malicious="$TEST_WORK_DIR/malicious.env"
write_valid_fixture "$malicious"
printf 'SAFE=$(touch %q)\n' "$EXECUTED_MARKER" >> "$malicious"
if load_launch_env_file "$malicious" SAFE VALUE >"$TEST_WORK_DIR/malicious.log" 2>&1; then
    printf 'malicious launch.env unexpectedly passed\n' >&2
    exit 1
fi
[[ ! -e "$EXECUTED_MARKER" ]]

extra="$TEST_WORK_DIR/extra.env"
write_valid_fixture "$extra"
printf '%s\n' 'EXTRA=unexpected' >> "$extra"
if load_launch_env_file "$extra" SAFE VALUE >"$TEST_WORK_DIR/extra.log" 2>&1; then
    printf 'extra launch.env key unexpectedly passed\n' >&2
    exit 1
fi

malformed="$TEST_WORK_DIR/malformed.env"
write_valid_fixture "$malformed"
printf '%s\n' 'SAFE=\x27not-a-printf-q-value\x27' >> "$malformed"
if load_launch_env_file "$malformed" SAFE VALUE >"$TEST_WORK_DIR/malformed.log" 2>&1; then
    printf 'malformed launch.env value unexpectedly passed\n' >&2
    exit 1
fi

duplicate="$TEST_WORK_DIR/duplicate.env"
write_valid_fixture "$duplicate"
printf '%s\n' 'SAFE=duplicate' >> "$duplicate"
if load_launch_env_file "$duplicate" SAFE VALUE >"$TEST_WORK_DIR/duplicate.log" 2>&1; then
    printf 'duplicate launch.env key unexpectedly passed\n' >&2
    exit 1
fi

empty="$TEST_WORK_DIR/empty.env"
write_valid_fixture "$empty"
printf '%s\n' 'SAFE=' >> "$empty"
if load_launch_env_file "$empty" SAFE VALUE >"$TEST_WORK_DIR/empty.log" 2>&1; then
    printf 'empty launch.env value unexpectedly passed\n' >&2
    exit 1
fi

printf 'load-launch-env parser fixture: ok\n'
