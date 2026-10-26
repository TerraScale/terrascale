umask 077
cat > selections.json <<'JSON'
[{"collection":"orders","ids":["01ARZ3NDEKTSV4RRFFQ69G5FAW","01ARZ3NDEKTSV4RRFFQ69G5FAX"]}]
JSON
ts -project "$PROJECT" recovery preview "$DATABASE" \
  '2026-10-25T10:00:00Z' selections.json > preview.json

# Stop and review preview.json. Then explicitly approve only the IDs you want.
cat > approved.json <<'JSON'
[{"collection":"orders","id":"01ARZ3NDEKTSV4RRFFQ69G5FAW"}]
JSON
ts -project "$PROJECT" recovery apply "$DATABASE" preview.json approved.json
# Read the approved records to verify the outcome, then remove local files.
rm preview.json approved.json selections.json
