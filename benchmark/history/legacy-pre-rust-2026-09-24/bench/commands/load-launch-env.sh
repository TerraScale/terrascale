#!/usr/bin/env bash

# Parse the metadata files written by the strict RustFS launchers without
# executing their contents.  These files historically used bash printf %q, so
# the parser accepts that representation but never evals or sources a receipt.
#
# Usage:
#   load_launch_env_file PATH KEY [KEY ...]
#
# The listed keys are the complete allowlist and are all required exactly once.
# Prefix a key with '?' when reading an older receipt where that key was not
# emitted yet; an optional key is still rejected if an unknown key is added.
# Parsed values are assigned to variables with the corresponding names in the
# caller's shell.  Callers should still validate values against their workload
# invariants after parsing.

launch_env_decode_word() {
    local encoded="$1"
    local output= ch next decoded escape digit i

    while [[ -n "$encoded" ]]; do
        ch="${encoded:0:1}"
        encoded="${encoded:1}"
        if [[ "$ch" != "\\" ]]; then
            output+="$ch"
            continue
        fi

        [[ -n "$encoded" ]] || return 1
        next="${encoded:0:1}"
        encoded="${encoded:1}"
        case "$next" in
            a|b|e|f|n|r|t|v)
                printf -v decoded '%b' "\\$next"
                ;;
            "\\"|"'"|\")
                decoded="$next"
                ;;
            0)
                escape='\0'
                for i in 1 2; do
                    digit="${encoded:0:1}"
                    [[ "$digit" =~ ^[0-7]$ ]] || break
                    escape+="$digit"
                    encoded="${encoded:1}"
                done
                printf -v decoded '%b' "$escape"
                ;;
            x)
                escape='\x'
                for i in 1 2; do
                    digit="${encoded:0:1}"
                    [[ "$digit" =~ ^[0-9A-Fa-f]$ ]] || break
                    escape+="$digit"
                    encoded="${encoded:1}"
                done
                [[ "$escape" != '\x' ]] || return 1
                printf -v decoded '%b' "$escape"
                ;;
            u)
                escape='\u'
                for i in 1 2 3 4; do
                    digit="${encoded:0:1}"
                    [[ "$digit" =~ ^[0-9A-Fa-f]$ ]] || return 1
                    escape+="$digit"
                    encoded="${encoded:1}"
                done
                printf -v decoded '%b' "$escape"
                ;;
            U)
                escape='\U'
                for i in 1 2 3 4 5 6 7 8; do
                    digit="${encoded:0:1}"
                    [[ "$digit" =~ ^[0-9A-Fa-f]$ ]] || return 1
                    escape+="$digit"
                    encoded="${encoded:1}"
                done
                printf -v decoded '%b' "$escape"
                ;;
            *)
                # printf %q backslash-quotes punctuation and whitespace. A
                # backslash before any other printable byte is just the
                # quoting marker; preserve the byte and drop the marker.
                decoded="$next"
                ;;
        esac
        output+="$decoded"
    done

    REPLY="$output"
}

launch_env_validate_unquoted_word() {
    local encoded="$1"
    local ch next
    while [[ -n "$encoded" ]]; do
        ch="${encoded:0:1}"
        encoded="${encoded:1}"
        if [[ "$ch" == "\\" ]]; then
            [[ -n "$encoded" ]] || return 1
            next="${encoded:0:1}"
            [[ ! "$next" =~ [[:cntrl:]] ]] || return 1
            encoded="${encoded:1}"
            continue
        fi
        [[ ! "$ch" =~ [[:cntrl:][:space:]] ]] || return 1
        case "$ch" in
            '$'|'`'|';'|'&'|'|'|'<'|'>'|'('|')'|'*'|'?'|'['|']'|'{'|'}'|'!'|'#'|"'"|'"')
                return 1
                ;;
        esac
    done
}

load_launch_env_file() {
    local launch_env_path="$1"
    shift

    [[ -n "$launch_env_path" && -f "$launch_env_path" && ! -L "$launch_env_path" ]] || {
        printf 'launch-env-parser: launch metadata is not a regular file: %s\n' "$launch_env_path" >&2
        return 1
    }

    local key line raw value required
    local -A allowed=() required_key seen=()
    for key in "$@"; do
        required=1
        if [[ "$key" == \?* ]]; then
            required=0
            key="${key:1}"
        fi
        [[ "$key" =~ ^[A-Z][A-Z0-9_]*$ ]] || {
            printf 'launch-env-parser: invalid allowlist key: %s\n' "$key" >&2
            return 1
        }
        [[ -z "${allowed[$key]+present}" ]] || {
            printf 'launch-env-parser: duplicate allowlist key: %s\n' "$key" >&2
            return 1
        }
        allowed["$key"]=1
        required_key["$key"]="$required"
    done
    (( ${#allowed[@]} > 0 )) || {
        printf 'launch-env-parser: empty allowlist\n' >&2
        return 1
    }

    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ ]] || {
            printf 'launch-env-parser: malformed launch metadata line\n' >&2
            return 1
        }
        key="${BASH_REMATCH[1]}"
        raw="${BASH_REMATCH[2]}"
        [[ -n "${allowed[$key]+present}" ]] || {
            printf 'launch-env-parser: unexpected launch metadata key: %s\n' "$key" >&2
            return 1
        }
        [[ -z "${seen[$key]+present}" ]] || {
            printf 'launch-env-parser: duplicate launch metadata key: %s\n' "$key" >&2
            return 1
        }

        # printf %q emits either a conservative unquoted word with backslash
        # escapes or a $'...' word for control characters.  Restrict the
        # lexical form before decoding with printf %b.  %b only processes
        # backslash escapes; it does not perform shell expansion, command
        # substitution, globbing, or redirection.
        if [[ "$raw" == "''" ]]; then
                value=
        elif [[ ${#raw} -ge 3 && ${raw:0:2} == "\$'" && ${raw: -1} == "'" ]]; then
                local quoted_pattern=$'^(\\\\[^[:cntrl:]]|[^\\\\\x27[:cntrl:]])*$'
                local inner="${raw:2:${#raw}-3}"
                [[ "$inner" =~ $quoted_pattern ]] || {
                    printf 'launch-env-parser: malformed quoted value for %s\n' "$key" >&2
                    return 1
                }
                launch_env_decode_word "$inner" || {
                    printf 'launch-env-parser: malformed quoted value for %s\n' "$key" >&2
                    return 1
                }
                value="$REPLY"
        else
                [[ -n "$raw" ]] || {
                    printf 'launch-env-parser: malformed value for %s\n' "$key" >&2
                    return 1
                }
                launch_env_validate_unquoted_word "$raw" || {
                    printf 'launch-env-parser: malformed value for %s\n' "$key" >&2
                    return 1
                }
                launch_env_decode_word "$raw" || {
                    printf 'launch-env-parser: malformed value for %s\n' "$key" >&2
                    return 1
                }
                value="$REPLY"
        fi

        # The key was matched against the allowlist above, so this assignment
        # cannot target an arbitrary shell variable or special parameter. A
        # caller may expose a readonly workload constant in the same name;
        # accept it only when the receipt repeats the exact constant.
        local declaration
        declaration="$(declare -p "$key" 2>/dev/null || true)"
        if [[ "$declaration" =~ ^declare[[:space:]]-[^[:space:]]*r ]]; then
            [[ "${!key}" == "$value" ]] || {
                printf 'launch-env-parser: readonly launch metadata key does not match: %s\n' "$key" >&2
                return 1
            }
        else
            printf -v "$key" '%s' "$value"
        fi
        seen["$key"]=1
    done < "$launch_env_path"

    for key in "${!allowed[@]}"; do
        [[ "${required_key[$key]}" == 0 || -n "${seen[$key]+present}" ]] || {
            printf 'launch-env-parser: missing launch metadata key: %s\n' "$key" >&2
            return 1
        }
    done
}
