// Package entityid implements the shared TerraScale entity ID contract.
package entityid

import (
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"strings"
	"unicode"
	"unicode/utf8"

	"github.com/oklog/ulid/v2"
)

// Entropy is process-wide and concurrency-safe, increasing within a millisecond.
var entropy = &ulid.LockedMonotonicReader{MonotonicReader: ulid.Monotonic(rand.Reader, 0)}

// New generates a canonical ULID and panics if ULID generation fails.
func New() string { return ulid.MustNew(ulid.Now(), entropy).String() }

// Check rejects noncanonical IDs without including input values in errors.
func Check(id string) error {
	u, err := ulid.ParseStrict(id)
	if err != nil || u.String() != id {
		return errors.New("entity ID must be a canonical uppercase 26-character ULID")
	}
	return nil
}

// DocumentKey checks a document ID: every document key is a canonical ULID.
func DocumentKey(key string) error { return Check(key) }

// StoredKey validates a stored read/checkpoint key, including user-chosen SQL
// primary keys. Length follows the producer's UTF-16 string length, without
// normalizing or changing the key. It must never validate document point input.
func StoredKey(key string) error {
	if key == "" || !utf8.ValidString(key) {
		return errors.New("stored key must be 1 to 256 UTF-16 code units without control characters")
	}
	units := 0
	for _, r := range key {
		if unicode.IsControl(r) {
			return errors.New("stored key must not contain control characters")
		}
		units++
		if r > 0xffff {
			units++
		}
		if units > 256 {
			return errors.New("stored key exceeds 256 UTF-16 code units")
		}
	}
	return nil
}

// Model checks explicitly tagged fields of SDK protocol models. Raw JSON,
// map keys, timestamps and untagged scalar values remain application data.
func Model(model any) error { return checkModel(reflect.ValueOf(model)) }

func checkModel(v reflect.Value) error {
	if !v.IsValid() {
		return nil
	}
	switch v.Kind() {
	case reflect.Pointer, reflect.Interface:
		if !v.IsNil() {
			return checkModel(v.Elem())
		}
	case reflect.Struct:
		for i := range v.NumField() {
			field, value := v.Type().Field(i), v.Field(i)
			if !field.IsExported() {
				continue
			}
			if field.Tag.Get("entity") == "payload" {
				if value.Kind() != reflect.Slice || value.Type().Elem().Kind() != reflect.Uint8 {
					return fmt.Errorf("%s: entity payload field must be a byte slice", field.Name)
				}
				if value.Len() > 0 {
					if err := Payload(value.Bytes()); err != nil {
						return err
					}
				}
				continue
			}
			mode, check := field.Tag.Get("ulid"), Check
			if keyMode := field.Tag.Get("documentkey"); keyMode != "" {
				mode, check = keyMode, DocumentKey
			}
			if storedMode := field.Tag.Get("storedkey"); storedMode != "" {
				mode, check = storedMode, StoredKey
			}
			if mode != "" {
				if value.Kind() == reflect.Pointer {
					if value.IsNil() {
						continue
					}
					value = value.Elem()
				}
				if mode == "optional" && value.String() == "" {
					continue
				}
				if err := check(value.String()); err != nil {
					return fmt.Errorf("%s: %w", field.Name, err)
				}
			} else if err := checkModel(value); err != nil {
				return err
			}
		}
	case reflect.Map:
		iter := v.MapRange()
		for iter.Next() {
			if err := checkModel(iter.Value()); err != nil {
				return err
			}
		}
	case reflect.Slice, reflect.Array:
		if v.Type().Elem().Kind() == reflect.Uint8 {
			return nil
		}
		for i := range v.Len() {
			if err := checkModel(v.Index(i)); err != nil {
				return err
			}
		}
	}
	return nil
}

// Path checks IDs following entity resource names in management routes.
func Path(path []string) error {
	for i := 1; i < len(path); i++ {
		switch path[i-1] {
		case "organizations", "projects", "databases", "members", "api-keys", "invitations":
			if path[i-1] == "invitations" && (path[i] == "accept" || path[i] == "lookup" || path[i] == "decline") {
				continue
			}
			if err := Check(path[i]); err != nil {
				return fmt.Errorf("%s ID: %w", path[i-1], err)
			}
		}
	}
	return nil
}

// Body checks management request references carried outside the URL.
func Body(data []byte) error {
	var body map[string]json.RawMessage
	if err := json.Unmarshal(data, &body); err != nil {
		return err
	}
	for name, raw := range body {
		if name == "id" || strings.HasSuffix(name, "Id") || strings.HasSuffix(name, "_id") {
			var id string
			if json.Unmarshal(raw, &id) != nil || Check(id) != nil {
				return fmt.Errorf("request %s: entity ID must be a canonical uppercase 26-character ULID", name)
			}
		}
	}
	return nil
}

// Payload enforces the document key as the sole entity ID.
func Payload(data []byte) error {
	var body map[string]json.RawMessage
	if err := json.Unmarshal(data, &body); err != nil {
		return err
	}
	if _, exists := body["id"]; exists {
		return errors.New("document payload must not contain id; use the document key")
	}
	return nil
}
