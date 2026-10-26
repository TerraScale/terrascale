umask 077
cat > selections.json <<'JSON'
[{"collection":"orders","ids":["01ARZ3NDEKTSV4RRFFQ69G5FAW","01ARZ3NDEKTSV4RRFFQ69G5FAX"]}]
JSON
api() {
  curl -sS --fail-with-body \
    -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"
}
RECOVERY_URL="https://api.terrascale.tech/v1/projects/$PROJECT/databases/$DATABASE/point-in-time"
jq -n --slurpfile selections selections.json \
  '{pointInTime:"2026-10-25T10:00:00Z",selections:$selections[0]}' > preview-request.json
api -X POST -H 'Content-Type: application/json' --data-binary @preview-request.json \
  "$RECOVERY_URL/recovery-preview" > http-preview.json

# Stop and review http-preview.json; explicitly choose the approved IDs.
cat > approved.json <<'JSON'
[{"collection":"orders","id":"01ARZ3NDEKTSV4RRFFQ69G5FAW"}]
JSON
jq -n --slurpfile preview http-preview.json --slurpfile approved approved.json \
  '{approval:$preview[0].approval,records:$approved[0]}' > apply-request.json
api -X POST -H 'Content-Type: application/json' --data-binary @apply-request.json \
  "$RECOVERY_URL/recover"
# Verify recovered records before deleting these private local files.
rm http-preview.json preview-request.json apply-request.json approved.json selections.json
