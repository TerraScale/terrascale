package sdk

import (
	"encoding/json"
	"math"
	"strings"
	"testing"
)

func TestNativeScalarsRejectLossyRepresentations(t *testing.T) {
	for _, raw := range []string{`0`, `"01"`, `"+1"`, `"-1"`, `"18446744073709551616"`, `null`, `"1.0"`} {
		var n DecimalU64
		if err := json.Unmarshal([]byte(raw), &n); err == nil {
			t.Errorf("accepted u64 %s", raw)
		}
	}
	var n DecimalU64
	if err := json.Unmarshal([]byte(`"18446744073709551615"`), &n); err != nil || uint64(n) != math.MaxUint64 {
		t.Fatalf("full u64 lost: %v %d", err, n)
	}
	for _, raw := range []string{`{"type":"i64","value":1}`, `{"type":"i64","value":"-0"}`, `{"type":"i64","value":"9223372036854775808"}`, `{"type":"bytes","value":"AQ=="}`, `{"type":"bytes","value":"AR"}`, `{"type":"bool","value":null}`, `{"type":"null","value":null}`, `{"type":"map","fields":[{"id":0,"value":{"type":"null"}}]}`} {
		var v Value
		if err := json.Unmarshal([]byte(raw), &v); err == nil {
			t.Errorf("accepted native value %s", raw)
		}
	}
	value := I64Value(math.MinInt64)
	encoded, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	var decoded Value
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatal(err)
	}
	if got, ok := decoded.Int64(); !ok || got != math.MinInt64 {
		t.Fatalf("i64 lost: %d", got)
	}
}

func TestNativeValueOwnershipAndProducerLimits(t *testing.T) {
	data := []byte{1, 2}
	value, err := BytesValue(data)
	if err != nil {
		t.Fatal(err)
	}
	data[0] = 9
	copy, _ := value.Bytes()
	copy[0] = 8
	got, _ := value.Bytes()
	if got[0] != 1 {
		t.Fatal("bytes are borrowed")
	}
	if _, err := BytesValue(make([]byte, (1<<20)+1)); err == nil {
		t.Fatal("oversized bytes accepted")
	}
	if _, err := MapValue(make([]DocumentField, 1025)); err == nil {
		t.Fatal("oversized map accepted")
	}
	if _, err := ArrayValue(make([]Value, 1025)); err == nil {
		t.Fatal("oversized array accepted")
	}
	array, err := ArrayValue([]Value{NullValue()})
	if err != nil {
		t.Fatal(err)
	}
	wrapped, err := MapValue([]DocumentField{{ID: 1, Value: array}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := ArrayValue([]Value{wrapped}); err == nil {
		t.Fatal("array descendant through map accepted")
	}
	deep := NullValue()
	for range 15 {
		deep, err = MapValue([]DocumentField{{ID: 1, Value: deep}})
		if err != nil {
			t.Fatal(err)
		}
	}
	if _, err := MapValue([]DocumentField{{ID: 1, Value: deep}}); err == nil {
		t.Fatal("depth17 accepted")
	}
	text, err := UTF8Value(strings.Repeat("x", (1<<20)-111))
	if err != nil {
		t.Fatal(err)
	}
	if err := (WriteValue{Kind: "live", Fields: []DocumentField{{ID: 1, Value: text}}}).validate(); err != nil {
		t.Fatal(err)
	}
	text, _ = UTF8Value(strings.Repeat("x", (1<<20)-110))
	if err := (WriteValue{Kind: "live", Fields: []DocumentField{{ID: 1, Value: text}}}).validate(); err == nil {
		t.Fatal("encoded document larger than1MiB accepted")
	}
}
