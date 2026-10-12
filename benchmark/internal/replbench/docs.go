package replbench

import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"math/rand/v2"
	"slices"
	"strconv"
)

// Hash is the first 16 bytes of SHA-256 over "id, tab, canonical document": what a region must hold for one id.
type Hash [16]byte

// DocHash hashes one document by id and canonical JSON.
func DocHash(id string, canonical []byte) Hash {
	h := sha256.New()
	h.Write([]byte(id))
	h.Write([]byte{'\t'})
	h.Write(canonical)
	var out Hash
	copy(out[:], h.Sum(nil))
	return out
}

// Canonical re-serializes JSON with object keys sorted at every depth, no insignificant whitespace, numbers kept as
// written and no HTML escaping, so equal documents compare equal whatever order a region stored them in.
func Canonical(raw []byte) ([]byte, error) {
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.UseNumber()
	var v any
	if err := dec.Decode(&v); err != nil {
		return nil, err
	}
	var b bytes.Buffer
	if err := writeCanonical(&b, v); err != nil {
		return nil, err
	}
	return b.Bytes(), nil
}

func writeCanonical(b *bytes.Buffer, v any) error {
	switch x := v.(type) {
	case map[string]any:
		keys := make([]string, 0, len(x))
		for k := range x {
			keys = append(keys, k)
		}
		slices.Sort(keys)
		b.WriteByte('{')
		for i, k := range keys {
			if i > 0 {
				b.WriteByte(',')
			}
			writeString(b, k)
			b.WriteByte(':')
			if err := writeCanonical(b, x[k]); err != nil {
				return err
			}
		}
		b.WriteByte('}')
	case []any:
		b.WriteByte('[')
		for i, e := range x {
			if i > 0 {
				b.WriteByte(',')
			}
			if err := writeCanonical(b, e); err != nil {
				return err
			}
		}
		b.WriteByte(']')
	case string:
		writeString(b, x)
	case json.Number:
		b.WriteString(x.String())
	case bool:
		b.WriteString(strconv.FormatBool(x))
	case nil:
		b.WriteString("null")
	default:
		return fmt.Errorf("unexpected JSON value %T", v)
	}
	return nil
}

func writeString(b *bytes.Buffer, s string) {
	enc := json.NewEncoder(b)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(s)
	b.Truncate(b.Len() - 1) // Encode appends a newline.
}

// Body is document i of the load, written by region, exactly size bytes of canonical JSON (or the smallest valid body
// when size is smaller): {"g":i%16,"i":i,"p":"<padding>","r":"<region>"}. The padding comes from seed and i, so the
// expected content of every document can be rebuilt without storing it.
func Body(seed uint64, i int, region string, size int) []byte {
	head := `{"g":` + strconv.Itoa(i%16) + `,"i":` + strconv.Itoa(i) + `,"p":"`
	tail := `","r":"` + region + `"}`
	pad := max(0, size-len(head)-len(tail))
	b := make([]byte, 0, len(head)+pad+len(tail))
	b = append(b, head...)
	r := rand.New(rand.NewPCG(seed, uint64(i)))
	for range pad {
		b = append(b, byte('a'+r.IntN(26)))
	}
	return append(b, tail...)
}
