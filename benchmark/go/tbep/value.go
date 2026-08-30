package tbep

import (
	"bytes"
	"encoding/json"
	"fmt"
	"sort"

	terrabasev1 "git.terrascale.tech/TerraScale/TerraBase/go/tbep/gen/terrabase/v1"
)

// ValueFromJSON converts one JSON document into the protocol's Value.
// JSON numbers that fit an integer become integers; the others become
// doubles. Object key order is canonicalized to ascending name, the
// engine's canonical object form.
func ValueFromJSON(document []byte) (*terrabasev1.Value, error) {
	decoder := json.NewDecoder(bytes.NewReader(document))
	decoder.UseNumber()
	var decoded any
	if err := decoder.Decode(&decoded); err != nil {
		return nil, fmt.Errorf("tbep: the document is not JSON: %w", err)
	}
	return valueFromAny(decoded), nil
}

func valueFromAny(decoded any) *terrabasev1.Value {
	switch typed := decoded.(type) {
	case nil:
		return &terrabasev1.Value{Kind: &terrabasev1.Value_Null{Null: terrabasev1.NullValue_NULL}}
	case bool:
		return &terrabasev1.Value{Kind: &terrabasev1.Value_Boolean{Boolean: typed}}
	case json.Number:
		if integer, err := typed.Int64(); err == nil {
			return &terrabasev1.Value{Kind: &terrabasev1.Value_Integer{Integer: integer}}
		}
		number, err := typed.Float64()
		if err != nil {
			return &terrabasev1.Value{Kind: &terrabasev1.Value_Null{Null: terrabasev1.NullValue_NULL}}
		}
		return &terrabasev1.Value{Kind: &terrabasev1.Value_Number{Number: number}}
	case string:
		return &terrabasev1.Value{Kind: &terrabasev1.Value_Text{Text: typed}}
	case []any:
		items := make([]*terrabasev1.Value, 0, len(typed))
		for _, item := range typed {
			items = append(items, valueFromAny(item))
		}
		return &terrabasev1.Value{Kind: &terrabasev1.Value_List{
			List: &terrabasev1.ValueList{Items: items},
		}}
	case map[string]any:
		names := make([]string, 0, len(typed))
		for name := range typed {
			names = append(names, name)
		}
		sort.Strings(names)
		entries := make([]*terrabasev1.ValueEntry, 0, len(names))
		for _, name := range names {
			entries = append(entries, &terrabasev1.ValueEntry{
				Name:  name,
				Value: valueFromAny(typed[name]),
			})
		}
		return &terrabasev1.Value{Kind: &terrabasev1.Value_Map{
			Map: &terrabasev1.ValueMap{Entries: entries},
		}}
	default:
		return &terrabasev1.Value{Kind: &terrabasev1.Value_Null{Null: terrabasev1.NullValue_NULL}}
	}
}

// JSONFromValue converts one protocol Value into JSON. Objects encode
// with ascending key order (Go marshals map keys sorted), so identical
// values encode to identical bytes.
func JSONFromValue(value *terrabasev1.Value) ([]byte, error) {
	if value == nil {
		return []byte("null"), nil
	}
	encoded, err := json.Marshal(anyFromValue(value))
	if err != nil {
		return nil, fmt.Errorf("tbep: the value does not encode: %w", err)
	}
	return encoded, nil
}

func anyFromValue(value *terrabasev1.Value) any {
	if value == nil || value.Kind == nil {
		return nil
	}
	switch typed := value.Kind.(type) {
	case *terrabasev1.Value_Null:
		return nil
	case *terrabasev1.Value_Boolean:
		return typed.Boolean
	case *terrabasev1.Value_Integer:
		return typed.Integer
	case *terrabasev1.Value_Number:
		return typed.Number
	case *terrabasev1.Value_Text:
		return typed.Text
	case *terrabasev1.Value_List:
		items := make([]any, 0, len(typed.List.GetItems()))
		for _, item := range typed.List.GetItems() {
			items = append(items, anyFromValue(item))
		}
		return items
	case *terrabasev1.Value_Map:
		object := make(map[string]any, len(typed.Map.GetEntries()))
		for _, entry := range typed.Map.GetEntries() {
			object[entry.GetName()] = anyFromValue(entry.GetValue())
		}
		return object
	default:
		return nil
	}
}
