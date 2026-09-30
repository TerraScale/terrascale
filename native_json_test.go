package sdk

import (
	"strings"
	"testing"
)

func TestNativeJSONRejectsAmbiguousInput(t *testing.T) {
	for _, raw := range []string{
		`{"version":1,"version":1,"state":"absent","state_version":null,"context":[],"siblings":[]}`,
		`{"Version":1,"state":"absent","state_version":null,"context":[],"siblings":[]}`,
		`{"version":1,"state":"absent","context":[],"siblings":[]}`,
		`{"version":1,"state":"absent","state_version":null,"context":null,"siblings":[]}`,
		`{"version":1,"state":"absent","state_version":null,"context":[],"siblings":[]} {}`,
		`{"version":1,"state":"\ud800","state_version":null,"context":[],"siblings":[]}`,
		`{"version":1,"state":"\udc00","state_version":null,"context":[],"siblings":[]}`,
		strings.Replace(nativeAbsentPoint, "absent", string([]byte{0xff}), 1),
	} {
		var point DocumentPoint
		if err := decodeNativeJSON([]byte(raw), &point); err == nil {
			t.Errorf("accepted ambiguous JSON: %q", raw)
		}
	}
	if err := nativeJSON([]byte(`{"emoji":"\ud83c\udf0d"}`)); err != nil {
		t.Fatal(err)
	}
}
