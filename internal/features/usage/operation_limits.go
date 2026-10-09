package usage

import (
	"net/url"
	"strconv"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

// Pointer fields distinguish removing a limit from leaving it unchanged.
type operationLimitChange struct {
	ReadLimit        *int64 `json:"readLimit,omitempty" jsonschema:"Monthly read limit; 0 removes it. Omit to leave it unchanged."`
	WriteLimit       *int64 `json:"writeLimit,omitempty" jsonschema:"Monthly write limit; 0 removes it. Omit to leave it unchanged."`
	ReplicationLimit *int64 `json:"replicationLimit,omitempty" jsonschema:"Monthly replication limit; 0 removes it. Omit to leave it unchanged."`
}

func (change operationLimitChange) valid() bool {
	for _, value := range []*int64{change.ReadLimit, change.WriteLimit, change.ReplicationLimit} {
		if value != nil && *value < 0 {
			return false
		}
	}
	return true
}

func (change operationLimitChange) apply(limits *core.OperationLimits) {
	if change.ReadLimit != nil {
		limits.ReadLimit = *change.ReadLimit
	}
	if change.WriteLimit != nil {
		limits.WriteLimit = *change.WriteLimit
	}
	if change.ReplicationLimit != nil {
		limits.ReplicationLimit = *change.ReplicationLimit
	}
}

func operationsFromForm(values url.Values, prefix string) (map[string]operationLimitChange, bool) {
	changes := make(map[string]operationLimitChange)
	for _, category := range []string{"Read", "Write", "Replication"} {
		keyPrefix := prefix + category + "_"
		for key, items := range values {
			if !strings.HasPrefix(key, keyPrefix) {
				continue
			}
			id := strings.TrimPrefix(key, keyPrefix)
			if id == "" || len(items) != 1 {
				return nil, false
			}
			text := strings.TrimSpace(items[0])
			var value int64
			if text != "" {
				// ParseInt accepts a leading sign; the form accepts digits only.
				if strings.IndexFunc(text, func(r rune) bool { return r < '0' || r > '9' }) >= 0 {
					return nil, false
				}
				var err error
				value, err = strconv.ParseInt(text, 10, 64)
				if err != nil {
					return nil, false
				}
			}
			change := changes[id]
			switch category {
			case "Read":
				change.ReadLimit = &value
			case "Write":
				change.WriteLimit = &value
			case "Replication":
				change.ReplicationLimit = &value
			}
			changes[id] = change
		}
	}
	return changes, true
}

func operationValue(value int64) string {
	if value <= 0 {
		return ""
	}
	return strconv.FormatInt(value, 10)
}
