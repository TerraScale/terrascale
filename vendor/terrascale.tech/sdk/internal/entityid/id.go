// Package entityid implements the shared TerraScale entity ID contract.
package entityid

import (
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"strings"

	"github.com/oklog/ulid/v2"
)

// New generates a canonical ULID using process-wide, concurrency-safe
// entropy that increases monotonically within a millisecond.
var entropy = &ulid.LockedMonotonicReader{MonotonicReader: ulid.Monotonic(rand.Reader, 0)}

func New() string { return ulid.MustNew(ulid.Now(), entropy).String() }

// Check rejects noncanonical IDs without including input values in errors.
func Check(id string) error {
	u, err := ulid.ParseStrict(id)
	if err != nil || u.String() != id {
		return errors.New("entity ID must be a canonical uppercase 26-character ULID")
	}
	return nil
}

// Model checks explicitly tagged fields of SDK protocol models. Raw JSON,
// maps, timestamps and other untagged scalar values remain application data.
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
			if mode := field.Tag.Get("ulid"); mode != "" {
				if value.Kind() == reflect.Pointer {
					if value.IsNil() {
						continue
					}
					value = value.Elem()
				}
				if mode == "optional" && value.String() == "" {
					continue
				}
				if err := Check(value.String()); err != nil {
					return fmt.Errorf("%s: %w", field.Name, err)
				}
			} else if err := checkModel(value); err != nil {
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
				return err
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
				return errors.New("request entity ID must be a canonical uppercase 26-character ULID")
			}
		}
	}
	return nil
}
