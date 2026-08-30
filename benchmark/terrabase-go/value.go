// Package terrabase is the official Go client for the TerraBase engine.
//
// It speaks TBEP, the TerraBase engine protocol, over gRPC against the
// tonic server of crates/terrabase-tbep. Per AGENTS.md it is the only
// client allowed to communicate with TerraBase.
package terrabase

import (
	"math"
	"unicode/utf8"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

// The declared value bounds, enforced fail-closed by the engine and
// mirrored here so malformed values are refused before the wire.
const (
	maxTextBytes    = 1 << 20 // 1 MiB of value text.
	maxListItems    = 4096
	maxMapEntries   = 1024
	maxNameBytes    = 64
	maxValueNodes   = 1 << 16 // cumulative nodes in one recursive value
	maxDetailIndent = 64      // one recursion guard for cyclic input
)

// Value is one recursive TerraBase document value: the payload of
// document bodies, schema declarations, and sync fields.
//
// The closed set of representations is:
//
//	nil                the closed null marker
//	bool
//	int64              any Go integer kind is accepted and normalized
//	float64            float32 is accepted and normalized
//	string             at most 1 MiB
//	[]Value            at most 4096 items
//	map[string]Value   at most 1024 entries, names of 1..=64 bytes
//
// Objects are name-sorted canonically by the engine; clients treat them
// as maps. Value is an alias of any, so literals assign directly:
//
//	body := terrabase.Value(map[string]terrabase.Value{
//		"title": "Designing Data-Intensive Applications",
//		"pages": 616,
//	})
type Value any

// valueToProto converts one Go value into the protobuf value, refusing
// representations outside the closed set with a local error.
func valueToProto(value Value) (*v1.Value, error) {
	budget := maxValueNodes
	return valueToProtoDepth(value, 0, &budget)
}

func valueToProtoDepth(value Value, depth int, budget *int) (*v1.Value, error) {
	if depth > maxDetailIndent {
		return nil, localError("value nesting exceeds %d levels", maxDetailIndent)
	}
	if *budget == 0 {
		return nil, localError("value exceeds %d cumulative nodes", maxValueNodes)
	}
	*budget--
	switch typed := value.(type) {
	case nil:
		return &v1.Value{Kind: &v1.Value_Null{Null: v1.NullValue_NULL_VALUE_NULL}}, nil
	case bool:
		return &v1.Value{Kind: &v1.Value_Boolean{Boolean: typed}}, nil
	case int:
		return int64ToProto(int64(typed))
	case int8:
		return int64ToProto(int64(typed))
	case int16:
		return int64ToProto(int64(typed))
	case int32:
		return int64ToProto(int64(typed))
	case int64:
		return int64ToProto(typed)
	case uint:
		return uint64ToProto(uint64(typed))
	case uint8:
		return uint64ToProto(uint64(typed))
	case uint16:
		return uint64ToProto(uint64(typed))
	case uint32:
		return uint64ToProto(uint64(typed))
	case uint64:
		return uint64ToProto(typed)
	case float32:
		if math.IsNaN(float64(typed)) || math.IsInf(float64(typed), 0) {
			return nil, localError("value number must be finite")
		}
		return &v1.Value{Kind: &v1.Value_Number{Number: float64(typed)}}, nil
	case float64:
		if math.IsNaN(typed) || math.IsInf(typed, 0) {
			return nil, localError("value number must be finite")
		}
		return &v1.Value{Kind: &v1.Value_Number{Number: typed}}, nil
	case string:
		if len(typed) > maxTextBytes {
			return nil, localError("value text exceeds %d bytes", maxTextBytes)
		}
		if !utf8.ValidString(typed) {
			return nil, localError("value text is not valid UTF-8")
		}
		return &v1.Value{Kind: &v1.Value_Text{Text: typed}}, nil
	case []Value:
		if len(typed) > maxListItems {
			return nil, localError("value list exceeds %d items", maxListItems)
		}
		if err := consumeValueBudget(budget, len(typed)); err != nil {
			return nil, err
		}
		items := make([]*v1.Value, 0, len(typed))
		for index, item := range typed {
			converted, err := valueToProtoDepth(item, depth+1, budget)
			if err != nil {
				return nil, localError("list item %d: %s", index, localDetail(err))
			}
			items = append(items, converted)
		}
		return &v1.Value{Kind: &v1.Value_List{List: &v1.ValueList{Items: items}}}, nil
	case map[string]Value:
		if len(typed) > maxMapEntries {
			return nil, localError("value map exceeds %d entries", maxMapEntries)
		}
		if err := consumeValueBudget(budget, len(typed)); err != nil {
			return nil, err
		}
		entries := make([]*v1.ValueEntry, 0, len(typed))
		for name, item := range typed {
			if name == "" || len(name) > maxNameBytes {
				return nil, localError("value map name must be 1..=%d bytes", maxNameBytes)
			}
			if !utf8.ValidString(name) || hasControl(name) {
				return nil, localError("value map name carries invalid characters")
			}
			converted, err := valueToProtoDepth(item, depth+1, budget)
			if err != nil {
				return nil, localError("map field %q: %s", name, localDetail(err))
			}
			entries = append(entries, &v1.ValueEntry{Name: name, Value: converted})
		}
		return &v1.Value{Kind: &v1.Value_Map{Map: &v1.ValueMap{Entries: entries}}}, nil
	default:
		return nil, localError("value of type %T is outside the closed value set", value)
	}
}

// localDetail extracts the bounded detail of one local refusal, for
// nesting without re-prefixing.
func localDetail(err error) string {
	if typed, ok := err.(*Error); ok {
		return typed.Detail
	}
	return err.Error()
}

func int64ToProto(number int64) (*v1.Value, error) {
	return &v1.Value{Kind: &v1.Value_Integer{Integer: number}}, nil
}

func uint64ToProto(number uint64) (*v1.Value, error) {
	if number > 1<<63-1 {
		return nil, localError("integer %d overflows the sint64 value range", number)
	}
	return int64ToProto(int64(number))
}

// valueFromProto converts one protobuf value into its Go
// representation. Objects decode as map[string]Value, lists as
// []Value; a value carrying no kind is refused.
func valueFromProto(value *v1.Value) (Value, error) {
	budget := maxValueNodes
	return valueFromProtoDepth(value, 0, &budget)
}

func valueFromProtoDepth(value *v1.Value, depth int, budget *int) (Value, error) {
	if depth > maxDetailIndent {
		return nil, localError("the engine sent a value nested beyond %d levels", maxDetailIndent)
	}
	if *budget == 0 {
		return nil, localError("the engine sent a value beyond %d cumulative nodes", maxValueNodes)
	}
	*budget--
	if value == nil {
		return nil, localError("the engine sent a value carrying no kind")
	}
	switch kind := value.GetKind().(type) {
	case *v1.Value_Null:
		return nil, nil
	case *v1.Value_Boolean:
		return kind.Boolean, nil
	case *v1.Value_Integer:
		return kind.Integer, nil
	case *v1.Value_Number:
		if math.IsNaN(kind.Number) || math.IsInf(kind.Number, 0) {
			return nil, localError("the engine sent a non-finite number")
		}
		return kind.Number, nil
	case *v1.Value_Text:
		if len(kind.Text) > maxTextBytes {
			return nil, localError("the engine sent value text beyond %d bytes", maxTextBytes)
		}
		return kind.Text, nil
	case *v1.Value_List:
		if len(kind.List.GetItems()) > maxListItems {
			return nil, localError("the engine sent a value list beyond %d items", maxListItems)
		}
		if err := consumeValueBudget(budget, len(kind.List.GetItems())); err != nil {
			return nil, err
		}
		items := make([]Value, 0, len(kind.List.GetItems()))
		for _, item := range kind.List.GetItems() {
			converted, err := valueFromProtoDepth(item, depth+1, budget)
			if err != nil {
				return nil, err
			}
			items = append(items, converted)
		}
		return items, nil
	case *v1.Value_Map:
		if len(kind.Map.GetEntries()) > maxMapEntries {
			return nil, localError("the engine sent a value map beyond %d entries", maxMapEntries)
		}
		if err := consumeValueBudget(budget, len(kind.Map.GetEntries())); err != nil {
			return nil, err
		}
		entries := make(map[string]Value, len(kind.Map.GetEntries()))
		for _, entry := range kind.Map.GetEntries() {
			if entry.GetName() == "" || len(entry.GetName()) > maxNameBytes {
				return nil, localError("the engine sent a map name outside 1..=%d bytes", maxNameBytes)
			}
			if _, exists := entries[entry.GetName()]; exists {
				return nil, localError("the engine sent duplicate map name %q", entry.GetName())
			}
			converted, err := valueFromProtoDepth(entry.GetValue(), depth+1, budget)
			if err != nil {
				return nil, localError("map field %q: %s", entry.GetName(), localDetail(err))
			}
			entries[entry.GetName()] = converted
		}
		return entries, nil
	default:
		return nil, localError("the engine sent a value outside the closed value set")
	}
}

func consumeValueBudget(budget *int, amount int) error {
	if *budget < amount {
		return localError("value exceeds %d cumulative allocations", maxValueNodes)
	}
	*budget -= amount
	return nil
}
