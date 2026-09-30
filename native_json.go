package sdk

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"reflect"
	"strconv"
	"strings"
	"unicode/utf8"
)

const MaxNativeBodyBytes = 4 << 20

// nativeJSON validates the complete JSON before decoding. encoding/json alone
// accepts duplicate names, invalid UTF-8 and case-insensitive struct members.
func nativeJSON(data []byte) error {
	if len(data) > MaxNativeBodyBytes || !utf8.Valid(data) {
		return fmt.Errorf("sdk: native JSON exceeds its byte limit or contains invalid UTF-8")
	}
	if err := nativeUnicode(data); err != nil {
		return err
	}
	d := json.NewDecoder(bytes.NewReader(data))
	d.UseNumber()
	nodes := 0
	if err := nativeJSONValue(d, 0, &nodes); err != nil {
		return err
	}
	if _, err := d.Token(); err != io.EOF {
		return fmt.Errorf("sdk: native JSON contains trailing data")
	}
	return nil
}

func nativeUnicode(data []byte) error {
	inside := false
	for i := 0; i < len(data); i++ {
		if data[i] == '"' {
			inside = !inside
			continue
		}
		if !inside || data[i] != '\\' {
			continue
		}
		if i+1 >= len(data) {
			return fmt.Errorf("sdk: truncated JSON escape")
		}
		if data[i+1] != 'u' {
			i++
			continue
		}
		if i+6 > len(data) {
			return fmt.Errorf("sdk: truncated Unicode escape")
		}
		value, err := strconv.ParseUint(string(data[i+2:i+6]), 16, 16)
		if err != nil {
			return err
		}
		if value >= 0xd800 && value <= 0xdbff {
			if i+12 > len(data) || data[i+6] != '\\' || data[i+7] != 'u' {
				return fmt.Errorf("sdk: unpaired JSON high surrogate")
			}
			low, err := strconv.ParseUint(string(data[i+8:i+12]), 16, 16)
			if err != nil || low < 0xdc00 || low > 0xdfff {
				return fmt.Errorf("sdk: unpaired JSON high surrogate")
			}
			i += 11
		} else if value >= 0xdc00 && value <= 0xdfff {
			return fmt.Errorf("sdk: unpaired JSON low surrogate")
		} else {
			i += 5
		}
	}
	return nil
}

func nativeJSONValue(d *json.Decoder, depth int, nodes *int) error {
	*nodes++
	if depth > 64 || *nodes > 1<<20 {
		return fmt.Errorf("sdk: native JSON nesting or node limit")
	}
	token, err := d.Token()
	if err != nil {
		return err
	}
	delim, ok := token.(json.Delim)
	if !ok {
		return nil
	}
	switch delim {
	case '{':
		seen := make(map[string]bool)
		for d.More() {
			key, err := d.Token()
			if err != nil {
				return err
			}
			name, ok := key.(string)
			if !ok || seen[name] {
				return fmt.Errorf("sdk: duplicate native JSON member %q", name)
			}
			seen[name] = true
			if err := nativeJSONValue(d, depth+1, nodes); err != nil {
				return err
			}
		}
	case '[':
		for d.More() {
			if err := nativeJSONValue(d, depth+1, nodes); err != nil {
				return err
			}
		}
	default:
		return fmt.Errorf("sdk: invalid native JSON delimiter")
	}
	_, err = d.Token()
	return err
}

func nativeObject(data []byte, required, optional []string) (map[string]json.RawMessage, error) {
	if err := nativeJSON(data); err != nil {
		return nil, err
	}
	var object map[string]json.RawMessage
	if err := json.Unmarshal(data, &object); err != nil || object == nil {
		return nil, fmt.Errorf("sdk: native JSON must be an object")
	}
	allowed := make(map[string]bool, len(required)+len(optional))
	for _, name := range required {
		allowed[name] = true
		if _, ok := object[name]; !ok {
			return nil, fmt.Errorf("sdk: native JSON member %q is required", name)
		}
	}
	for _, name := range optional {
		allowed[name] = true
	}
	for name := range object {
		if !allowed[name] {
			return nil, fmt.Errorf("sdk: unknown native JSON member %q", name)
		}
	}
	return object, nil
}

func decodeNativeJSON(data []byte, out any) error {
	if err := nativeJSON(data); err != nil {
		return err
	}
	t := reflect.TypeOf(out)
	if t == nil || t.Kind() != reflect.Pointer {
		return fmt.Errorf("sdk: native decoder requires a pointer")
	}
	if err := nativeShape(data, t.Elem()); err != nil {
		return err
	}
	return json.Unmarshal(data, out)
}

var nativeUnmarshaler = reflect.TypeFor[json.Unmarshaler]()

func nativeShape(data []byte, t reflect.Type) error {
	if t.Kind() == reflect.Pointer {
		if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
			return nil
		}
		return nativeShape(data, t.Elem())
	}
	if reflect.PointerTo(t).Implements(nativeUnmarshaler) {
		return nil // Tagged unions and protocol scalars validate their own shape.
	}
	switch t.Kind() {
	case reflect.Struct:
		var required, optional []string
		fields := make(map[string]reflect.Type)
		for i := range t.NumField() {
			field := t.Field(i)
			if !field.IsExported() {
				continue
			}
			name, options, _ := strings.Cut(field.Tag.Get("json"), ",")
			if name == "-" {
				continue
			}
			if name == "" {
				name = field.Name
			}
			fields[name] = field.Type
			if strings.Contains(options, "omitempty") || strings.Contains(options, "omitzero") {
				optional = append(optional, name)
			} else {
				required = append(required, name)
			}
		}
		object, err := nativeObject(data, required, optional)
		if err != nil {
			return err
		}
		for name, raw := range object {
			if err := nativeShape(raw, fields[name]); err != nil {
				return fmt.Errorf("sdk: native %s: %w", name, err)
			}
		}
	case reflect.Slice:
		if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
			return fmt.Errorf("sdk: native array cannot be null")
		}
		var array []json.RawMessage
		if err := json.Unmarshal(data, &array); err != nil {
			return err
		}
		for _, raw := range array {
			if err := nativeShape(raw, t.Elem()); err != nil {
				return err
			}
		}
	default:
		if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
			return fmt.Errorf("sdk: native scalar cannot be null")
		}
	}
	return nil
}
