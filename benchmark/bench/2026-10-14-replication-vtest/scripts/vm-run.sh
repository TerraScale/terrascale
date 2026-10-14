#!/usr/bin/env bash
# Runs a local shell script as root inside one ts-vtest-tb machine (faults image: sh, base64). Usage: vm-run.sh <machine> <script> [args...]
set -euo pipefail
m=${1:?machine}; script=${2:?script}; shift 2
b64=$(base64 -w0 < "$script")
flyctl ssh console -a ts-vtest-tb --machine "$m" -C "sh -c 'echo $b64 | base64 -d > /tmp/vm-run.sh && sh /tmp/vm-run.sh $*'"
