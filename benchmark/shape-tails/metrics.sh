#!/usr/bin/env bash
set -euo pipefail
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort) | head -1)
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
sudo -n nsenter -t "$pid" -n curl -fsS http://127.0.0.1:9091/metrics | grep -E '^(terrabase_cdc_retained_rows|terrabase_backup_lag|terrabase_backup_failures|terrabase_backup_uploaded|terrabase_build)' || true
