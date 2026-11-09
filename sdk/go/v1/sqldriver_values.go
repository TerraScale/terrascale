package v1

import (
	"database/sql/driver"
	"encoding/json"
	"strings"
	"time"
)

const dbTimeLayout = "2006-01-02 15:04:05.999999999-07:00"

var dbTimeParseLayouts = []string{
	dbTimeLayout,
	time.RFC3339Nano,
	"2006-01-02 15:04:05",
	"2006-01-02",
}

func (c *dbConn) CheckNamedValue(nv *driver.NamedValue) error {
	if t, ok := nv.Value.(time.Time); ok {
		nv.Value = t.UTC().Format(dbTimeLayout)
		return nil
	}
	switch nv.Value.(type) {
	case nil, string, json.Number, []byte, bool,
		int, int8, int16, int32, int64,
		uint, uint8, uint16, uint32, uint64,
		float32, float64:
		return nil
	}
	return driver.ErrSkip
}

func normalizeRowValue(v any, declType string) any {
	if s, ok := v.(string); ok && isTimeDeclType(declType) {
		for _, layout := range dbTimeParseLayouts {
			if t, err := time.Parse(layout, s); err == nil {
				return t
			}
		}
		return s
	}
	switch v := v.(type) {
	case nil, string, bool, []byte, int64, float64, time.Time:
		return v
	case json.Number:
		if i, err := v.Int64(); err == nil {
			return i
		}
		f, _ := v.Float64()
		return f
	case int:
		return int64(v)
	case int8:
		return int64(v)
	case int16:
		return int64(v)
	case int32:
		return int64(v)
	case uint:
		return int64(v)
	case uint8:
		return int64(v)
	case uint16:
		return int64(v)
	case uint32:
		return int64(v)
	case uint64:
		return int64(v)
	case float32:
		return float64(v)
	}
	return v
}

func isTimeDeclType(declType string) bool {
	switch strings.ToUpper(declType) {
	case "DATE", "DATETIME", "TIMESTAMP":
		return true
	}
	return false
}
