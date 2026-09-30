package sdk

import (
	"bytes"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"slices"
	"strconv"
	"strings"
	"unicode/utf8"
)

// UUID is a canonical lowercase UUID, including its four hyphens.
type UUID string

func ParseUUID(value string) (UUID, error) {
	if len(value) != 36 {
		return "", fmt.Errorf("sdk: UUID must be canonical lowercase text")
	}
	for i := range value {
		if i == 8 || i == 13 || i == 18 || i == 23 {
			if value[i] != '-' {
				return "", fmt.Errorf("sdk: UUID must be canonical lowercase text")
			}
		} else if !lowerHex(value[i]) {
			return "", fmt.Errorf("sdk: UUID must be canonical lowercase text")
		}
	}
	return UUID(value), nil
}

func (u UUID) MarshalJSON() ([]byte, error) {
	if _, err := ParseUUID(string(u)); err != nil {
		return nil, err
	}
	return json.Marshal(string(u))
}

func (u *UUID) UnmarshalJSON(data []byte) error {
	text, err := nativeString(data)
	if err != nil {
		return err
	}
	parsed, err := ParseUUID(text)
	if err == nil {
		*u = parsed
	}
	return err
}

func (u UUID) binary() []byte {
	data, _ := hex.DecodeString(strings.ReplaceAll(string(u), "-", ""))
	return data // All callers validate UUIDs before hashing.
}

// DecimalU64 preserves the complete unsigned range in the JSON string wire type.
type DecimalU64 uint64

func (n DecimalU64) MarshalJSON() ([]byte, error) {
	return json.Marshal(strconv.FormatUint(uint64(n), 10))
}
func (n *DecimalU64) UnmarshalJSON(data []byte) error {
	text, err := nativeString(data)
	if err != nil || text == "" || (len(text) > 1 && text[0] == '0') {
		return fmt.Errorf("sdk: expected canonical decimal u64 string")
	}
	for i := range text {
		if text[i] < '0' || text[i] > '9' {
			return fmt.Errorf("sdk: expected canonical decimal u64 string")
		}
	}
	value, err := strconv.ParseUint(text, 10, 64)
	if err != nil {
		return err
	}
	*n = DecimalU64(value)
	return nil
}

type Hex16 [16]byte
type Hex32 [32]byte

func ParseHex16(text string) (Hex16, error) {
	var value Hex16
	err := parseHex(text, value[:])
	return value, err
}
func ParseHex32(text string) (Hex32, error) {
	var value Hex32
	err := parseHex(text, value[:])
	return value, err
}
func (v Hex16) String() string               { return hex.EncodeToString(v[:]) }
func (v Hex32) String() string               { return hex.EncodeToString(v[:]) }
func (v Hex16) MarshalJSON() ([]byte, error) { return json.Marshal(v.String()) }
func (v Hex32) MarshalJSON() ([]byte, error) { return json.Marshal(v.String()) }
func (v *Hex16) UnmarshalJSON(data []byte) error {
	text, err := nativeString(data)
	if err != nil {
		return err
	}
	parsed, err := ParseHex16(text)
	if err == nil {
		*v = parsed
	}
	return err
}
func (v *Hex32) UnmarshalJSON(data []byte) error {
	text, err := nativeString(data)
	if err != nil {
		return err
	}
	parsed, err := ParseHex32(text)
	if err == nil {
		*v = parsed
	}
	return err
}
func lowerHex(c byte) bool { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' }
func parseHex(text string, out []byte) error {
	if len(text) != 2*len(out) {
		return fmt.Errorf("sdk: expected lowercase hex with %d bytes", len(out))
	}
	for i := range text {
		if !lowerHex(text[i]) {
			return fmt.Errorf("sdk: expected lowercase hex")
		}
	}
	_, err := hex.Decode(out, []byte(text))
	return err
}
func nativeString(data []byte) (string, error) {
	if len(bytes.TrimSpace(data)) == 0 || bytes.TrimSpace(data)[0] != '"' || !utf8.Valid(data) {
		return "", fmt.Errorf("sdk: native value must be a string")
	}
	var text string
	err := json.Unmarshal(data, &text)
	return text, err
}

type ValueKind string

const (
	ValueNull  ValueKind = "null"
	ValueBool  ValueKind = "bool"
	ValueI64   ValueKind = "i64"
	ValueUTF8  ValueKind = "utf8"
	ValueBytes ValueKind = "bytes"
	ValueMap   ValueKind = "map"
	ValueArray ValueKind = "array"
)

// Value is the native value union. Constructors own their buffers; accessors
// return copies so request effects cannot change through borrowed storage.
type Value struct {
	kind    ValueKind
	boolean bool
	integer int64
	text    string
	data    []byte
	fields  []DocumentField
	items   []Value
}

type DocumentField struct {
	ID    uint32 `json:"id"`
	Value Value  `json:"value"`
}

func NullValue() Value       { return Value{kind: ValueNull} }
func BoolValue(v bool) Value { return Value{kind: ValueBool, boolean: v} }
func I64Value(v int64) Value { return Value{kind: ValueI64, integer: v} }
func UTF8Value(v string) (Value, error) {
	if !utf8.ValidString(v) || len(v) > MaxNativeBodyBytes {
		return Value{}, fmt.Errorf("sdk: invalid or oversized UTF-8 value")
	}
	return Value{kind: ValueUTF8, text: v}, nil
}
func BytesValue(v []byte) (Value, error) {
	if len(v) > 1<<20 {
		return Value{}, fmt.Errorf("sdk: native bytes value exceeds 1 MiB")
	}
	return Value{kind: ValueBytes, data: bytes.Clone(v)}, nil
}
func MapValue(fields []DocumentField) (Value, error) {
	if len(fields) > 1024 {
		return Value{}, fmt.Errorf("sdk: native map child limit")
	}
	copy := slices.Clone(fields)
	slices.SortFunc(copy, func(a, b DocumentField) int {
		if a.ID < b.ID {
			return -1
		}
		if a.ID > b.ID {
			return 1
		}
		return 0
	})
	v := Value{kind: ValueMap, fields: copy}
	nodes := 0
	if _, _, err := v.encode(1, &nodes); err != nil {
		return Value{}, err
	}
	return v, nil
}
func ArrayValue(items []Value) (Value, error) {
	if len(items) > 1024 {
		return Value{}, fmt.Errorf("sdk: native array child limit")
	}
	v := Value{kind: ValueArray, items: slices.Clone(items)}
	nodes := 0
	if _, _, err := v.encode(1, &nodes); err != nil {
		return Value{}, err
	}
	return v, nil
}
func (v Value) Kind() ValueKind                 { return v.kind }
func (v Value) Bool() (bool, bool)              { return v.boolean, v.kind == ValueBool }
func (v Value) Int64() (int64, bool)            { return v.integer, v.kind == ValueI64 }
func (v Value) UTF8() (string, bool)            { return v.text, v.kind == ValueUTF8 }
func (v Value) Bytes() ([]byte, bool)           { return bytes.Clone(v.data), v.kind == ValueBytes }
func (v Value) Fields() ([]DocumentField, bool) { return slices.Clone(v.fields), v.kind == ValueMap }
func (v Value) Items() ([]Value, bool)          { return slices.Clone(v.items), v.kind == ValueArray }

func (v Value) MarshalJSON() ([]byte, error) {
	nodes := 0
	if _, _, err := v.encode(1, &nodes); err != nil {
		return nil, err
	}
	object := map[string]any{"type": v.kind}
	switch v.kind {
	case ValueNull:
	case ValueBool:
		object["value"] = v.boolean
	case ValueI64:
		object["value"] = strconv.FormatInt(v.integer, 10)
	case ValueUTF8:
		object["value"] = v.text
	case ValueBytes:
		object["value"] = base64.RawURLEncoding.EncodeToString(v.data)
	case ValueMap:
		object["fields"] = nonnil(v.fields)
	case ValueArray:
		object["items"] = nonnil(v.items)
	}
	return json.Marshal(object)
}

func (v *Value) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"type"}, []string{"value", "fields", "items"})
	if err != nil {
		return err
	}
	var kind ValueKind
	if err := json.Unmarshal(object["type"], &kind); err != nil {
		return err
	}
	field := "value"
	switch kind {
	case ValueNull:
		field = ""
	case ValueMap:
		field = "fields"
	case ValueArray:
		field = "items"
	case ValueBool, ValueI64, ValueUTF8, ValueBytes:
	default:
		return fmt.Errorf("sdk: unknown native value type %q", kind)
	}
	if field == "" && len(object) != 1 || field != "" && (len(object) != 2 || object[field] == nil) {
		return fmt.Errorf("sdk: native value has incorrect members")
	}
	value := Value{kind: kind}
	raw := object[field]
	switch kind {
	case ValueNull:
	case ValueBool:
		if bytes.Equal(bytes.TrimSpace(raw), []byte("null")) {
			return fmt.Errorf("sdk: bool cannot be null")
		}
		err = json.Unmarshal(raw, &value.boolean)
	case ValueI64:
		var text string
		text, err = nativeString(raw)
		if err == nil {
			value.integer, err = strconv.ParseInt(text, 10, 64)
		}
		if err == nil && strconv.FormatInt(value.integer, 10) != text {
			err = fmt.Errorf("sdk: i64 must be canonical decimal")
		}
	case ValueUTF8:
		value.text, err = nativeString(raw)
	case ValueBytes:
		var text string
		text, err = nativeString(raw)
		if err == nil {
			value.data, err = base64.RawURLEncoding.Strict().DecodeString(text)
		}
		if err == nil && base64.RawURLEncoding.EncodeToString(value.data) != text {
			err = fmt.Errorf("sdk: bytes must be canonical base64url")
		}
	case ValueMap:
		err = decodeNativeJSON(raw, &value.fields)
	case ValueArray:
		err = decodeNativeJSON(raw, &value.items)
	}
	if err != nil {
		return err
	}
	nodes := 0
	if _, _, err := value.encode(1, &nodes); err != nil {
		return err
	}
	*v = value
	return nil
}

func nonnil[T any](items []T) []T {
	if items == nil {
		return []T{}
	}
	return items
}

func (v Value) encode(depth int, nodes *int) (byte, []byte, error) {
	if err := v.validateArrays(false); err != nil {
		return 0, nil, err
	}
	*nodes++
	if depth > 16 || *nodes > 4096 {
		return 0, nil, fmt.Errorf("sdk: native document depth or node limit")
	}
	switch v.kind {
	case ValueNull:
		return 1, nil, nil
	case ValueBool:
		if v.boolean {
			return 2, []byte{1}, nil
		}
		return 2, []byte{0}, nil
	case ValueI64:
		return 3, binary.LittleEndian.AppendUint64(nil, uint64(v.integer)), nil
	case ValueUTF8:
		if !utf8.ValidString(v.text) || len(v.text) > MaxNativeBodyBytes {
			return 0, nil, fmt.Errorf("sdk: invalid UTF-8 value")
		}
		return 4, []byte(v.text), nil
	case ValueBytes:
		if len(v.data) > MaxNativeBodyBytes {
			return 0, nil, fmt.Errorf("sdk: oversized bytes value")
		}
		return 5, v.data, nil
	case ValueMap:
		payload, err := encodeFields(v.fields, depth+1, nodes, true)
		return 6, payload, err
	case ValueArray:
		fields := make([]DocumentField, len(v.items))
		for i, item := range v.items {
			fields[i].Value = item
		}
		payload, err := encodeFields(fields, depth+1, nodes, false)
		return 7, payload, err
	default:
		return 0, nil, fmt.Errorf("sdk: uninitialized native value")
	}
}

func (v Value) validateArrays(inside bool) error {
	if inside && v.kind == ValueArray {
		return fmt.Errorf("sdk: arrays cannot descend from arrays, including through maps")
	}
	for _, field := range v.fields {
		if err := field.Value.validateArrays(inside); err != nil {
			return err
		}
	}
	for _, item := range v.items {
		if err := item.validateArrays(true); err != nil {
			return err
		}
	}
	return nil
}

func encodeFields(fields []DocumentField, depth int, nodes *int, isMap bool) ([]byte, error) {
	if len(fields) > 1024 {
		return nil, fmt.Errorf("sdk: native field or item limit")
	}
	payload := binary.LittleEndian.AppendUint16(nil, uint16(len(fields)))
	var previous uint32
	for _, field := range fields {
		if isMap && (field.ID == 0 || field.ID <= previous) {
			return nil, fmt.Errorf("sdk: native fields must have sorted unique positive IDs")
		}
		previous = field.ID
		tag, value, err := field.Value.encode(depth, nodes)
		if err != nil {
			return nil, err
		}
		if isMap {
			payload = binary.LittleEndian.AppendUint32(payload, field.ID)
		}
		payload = append(payload, tag)
		payload = binary.LittleEndian.AppendUint32(payload, uint32(len(value)))
		payload = append(payload, value...)
		if len(payload) > MaxNativeBodyBytes {
			return nil, fmt.Errorf("sdk: native container byte limit")
		}
	}
	return payload, nil
}
