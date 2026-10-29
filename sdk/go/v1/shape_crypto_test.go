package v1

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hkdf"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"os"
	"slices"
	"strconv"
	"testing"
)

type shapeVectors struct {
	TBS1       []tbs1Vector    `json:"tbs1_vectors"`
	Rejections []tbs1Rejection `json:"tbs1_rejection_vectors"`
	Generated  []tbs1Generated `json:"tbs1_generated_vectors"`
}

type tbs1Frame struct {
	Index            string `json:"index"`
	LengthWordHex    string `json:"length_word_hex"`
	NonceHex         string `json:"nonce_hex"`
	AADHex           string `json:"aad_hex"`
	CiphertextSHA256 string `json:"ciphertext_sha256"`
	TagHex           string `json:"tag_hex"`
}

type tbs1Vector struct {
	ID                string      `json:"id"`
	Handle            string      `json:"handle"`
	RequestOffset     string      `json:"request_offset"`
	Epoch             string      `json:"epoch"`
	KeyID             string      `json:"key_id"`
	ProjectDataKeyHex string      `json:"project_data_key_hex"`
	EpochKeyHex       string      `json:"epoch_key_hex"`
	ObjectSaltHex     string      `json:"object_salt_hex"`
	BindingHex        string      `json:"binding_hex"`
	ObjectKeyHex      string      `json:"object_key_hex"`
	HeaderHex         string      `json:"header_hex"`
	Frames            []tbs1Frame `json:"frames"`
	TBS1Hex           string      `json:"tbs1_hex"`
	PlaintextUTF8     string      `json:"plaintext_utf8"`
	CiphertextSHA256  string      `json:"ciphertext_sha256"`
	CiphertextSize    int         `json:"ciphertext_size"`
}

type tbs1Rejection struct {
	ID             string `json:"id"`
	Handle         string `json:"handle"`
	RequestOffset  string `json:"request_offset"`
	Epoch          string `json:"epoch"`
	KeyID          string `json:"key_id"`
	EpochKeyHex    string `json:"epoch_key_hex"`
	TBS1Hex        string `json:"tbs1_hex"`
	ExpectedResult string `json:"expected_result"`
}

type tbs1Mutation struct {
	ID             string `json:"id"`
	Operation      string `json:"operation"`
	Index          int    `json:"index"`
	Indices        []int  `json:"indices"`
	ExpectedResult string `json:"expected_result"`
}

type tbs1Generated struct {
	ID                string      `json:"id"`
	Handle            string      `json:"handle"`
	RequestOffset     string      `json:"request_offset"`
	Epoch             string      `json:"epoch"`
	KeyID             string      `json:"key_id"`
	ProjectDataKeyHex string      `json:"project_data_key_hex"`
	EpochKeyHex       string      `json:"epoch_key_hex"`
	ObjectSaltHex     string      `json:"object_salt_hex"`
	BindingHex        string      `json:"binding_hex"`
	ObjectKeyHex      string      `json:"object_key_hex"`
	HeaderHex         string      `json:"header_hex"`
	Frames            []tbs1Frame `json:"frames"`
	CiphertextSHA256  string      `json:"ciphertext_sha256"`
	CiphertextSize    int         `json:"ciphertext_size"`
	PlaintextRecipe   struct {
		RepeatByteHex string `json:"repeat_byte_hex"`
		Length        int    `json:"length"`
	} `json:"plaintext_recipe"`
	Mutations []tbs1Mutation `json:"mutations"`
}

func TestDecryptShapeObjectVectors(t *testing.T) {
	for _, v := range loadShapeVectors(t).TBS1 {
		t.Run(v.ID, func(t *testing.T) {
			epochKey := mustHex(t, v.EpochKeyHex)
			salt := mustHex(t, v.ObjectSaltHex)
			header := mustHex(t, v.HeaderHex)
			obj := mustHex(t, v.TBS1Hex)

			ke, err := hkdf.Key(sha256.New, mustHex(t, v.ProjectDataKeyHex), nil, "TBS1 epoch\x00"+v.Handle+"\x00"+v.Epoch, 32)
			if err != nil {
				t.Fatal(err)
			}
			if !bytes.Equal(ke, epochKey) {
				t.Fatalf("K_e = %x, want %s", ke, v.EpochKeyHex)
			}
			binding := shapeObjectBinding(v.Handle, v.RequestOffset)
			if got := hex.EncodeToString(binding); got != v.BindingHex {
				t.Fatalf("binding = %s, want %s", got, v.BindingHex)
			}
			ko, err := shapeObjectKey(epochKey, salt, binding)
			if err != nil {
				t.Fatal(err)
			}
			if got := hex.EncodeToString(ko); got != v.ObjectKeyHex {
				t.Fatalf("K_o = %s, want %s", got, v.ObjectKeyHex)
			}
			checkShapeObjectSeal(t, obj, header, v.Frames, v.CiphertextSize, v.CiphertextSHA256)

			got, err := decryptShapeObject(obj, epochKey, v.Handle, v.RequestOffset, mustEpoch(t, v.Epoch), v.KeyID)
			if err != nil {
				t.Fatal(err)
			}
			if string(got) != v.PlaintextUTF8 {
				t.Fatalf("plaintext = %q, want %q", got, v.PlaintextUTF8)
			}
		})
	}
}

func TestDecryptShapeObjectRejectsVectors(t *testing.T) {
	for _, v := range loadShapeVectors(t).Rejections {
		t.Run(v.ID, func(t *testing.T) {
			if v.ExpectedResult != "reject_without_applying" {
				t.Fatalf("expected_result = %q", v.ExpectedResult)
			}
			got, err := decryptShapeObject(mustHex(t, v.TBS1Hex), mustHex(t, v.EpochKeyHex), v.Handle, v.RequestOffset, mustEpoch(t, v.Epoch), v.KeyID)
			if err == nil {
				t.Fatalf("accepted object, returned %d plaintext bytes", len(got))
			}
			if got != nil {
				t.Fatal("returned plaintext alongside an error")
			}
		})
	}
}

func TestDecryptShapeObjectGeneratedVectors(t *testing.T) {
	for _, v := range loadShapeVectors(t).Generated {
		t.Run(v.ID, func(t *testing.T) {
			epochKey := mustHex(t, v.EpochKeyHex)
			header := mustHex(t, v.HeaderHex)
			binding := shapeObjectBinding(v.Handle, v.RequestOffset)
			if got := hex.EncodeToString(binding); got != v.BindingHex {
				t.Fatalf("binding = %s, want %s", got, v.BindingHex)
			}
			ko, err := shapeObjectKey(epochKey, mustHex(t, v.ObjectSaltHex), binding)
			if err != nil {
				t.Fatal(err)
			}
			if got := hex.EncodeToString(ko); got != v.ObjectKeyHex {
				t.Fatalf("K_o = %s, want %s", got, v.ObjectKeyHex)
			}
			gcm := newShapeTestGCM(t, ko)
			plaintext := bytes.Repeat(mustHex(t, v.PlaintextRecipe.RepeatByteHex), v.PlaintextRecipe.Length)
			frames := sealShapeFrames(gcm, header, plaintext)
			obj := assembleShapeObject(header, frames)
			checkShapeObjectSeal(t, obj, header, v.Frames, v.CiphertextSize, v.CiphertextSHA256)

			epoch := mustEpoch(t, v.Epoch)
			got, err := decryptShapeObject(obj, epochKey, v.Handle, v.RequestOffset, epoch, v.KeyID)
			if err != nil {
				t.Fatal(err)
			}
			if !bytes.Equal(got, plaintext) {
				t.Fatalf("plaintext mismatch: %d bytes, want %d", len(got), len(plaintext))
			}

			for _, m := range v.Mutations {
				t.Run(m.ID, func(t *testing.T) {
					if m.ExpectedResult != "reject_without_applying" {
						t.Fatalf("expected_result = %q", m.ExpectedResult)
					}
					var mutated [][]byte
					switch m.Operation {
					case "swap_frames":
						mutated = slices.Clone(frames)
						mutated[m.Indices[0]], mutated[m.Indices[1]] = mutated[m.Indices[1]], mutated[m.Indices[0]]
					case "drop_frame":
						mutated = slices.Delete(slices.Clone(frames), m.Index, m.Index+1)
					case "set_last_flag_recompute_tag_and_drop_following_frames":
						chunk := plaintext[m.Index*shapeTBS1FrameMax : (m.Index+1)*shapeTBS1FrameMax]
						mutated = append(slices.Clone(frames[:m.Index]), sealShapeFrame(gcm, header, uint64(m.Index), true, chunk))
					default:
						t.Fatalf("unknown operation %q", m.Operation)
					}
					if _, err := decryptShapeObject(assembleShapeObject(header, mutated), epochKey, v.Handle, v.RequestOffset, epoch, v.KeyID); err == nil {
						t.Fatal("mutated object accepted")
					}
				})
			}
		})
	}
}

func TestDecryptShapeObjectSizeBounds(t *testing.T) {
	v := loadShapeVectors(t).Generated[0]
	epochKey := mustHex(t, v.EpochKeyHex)
	header := mustHex(t, v.HeaderHex)
	ko, err := shapeObjectKey(epochKey, mustHex(t, v.ObjectSaltHex), shapeObjectBinding(v.Handle, v.RequestOffset))
	if err != nil {
		t.Fatal(err)
	}
	gcm := newShapeTestGCM(t, ko)
	epoch := mustEpoch(t, v.Epoch)
	for _, tc := range []struct {
		name string
		size int
		ok   bool
	}{
		{"exact 8 MiB is accepted", 8 << 20, true},
		{"9 MiB is rejected", 9 << 20, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			plaintext := bytes.Repeat([]byte{0x61}, tc.size)
			obj := assembleShapeObject(header, sealShapeFrames(gcm, header, plaintext))
			got, err := decryptShapeObject(obj, epochKey, v.Handle, v.RequestOffset, epoch, v.KeyID)
			if !tc.ok {
				if err == nil {
					t.Fatalf("accepted %d bytes of plaintext", len(got))
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if !bytes.Equal(got, plaintext) {
				t.Fatalf("plaintext mismatch: %d bytes, want %d", len(got), len(plaintext))
			}
		})
	}
}

func TestDecryptShapeObjectRejectsBadInputs(t *testing.T) {
	v := loadShapeVectors(t).TBS1[0]
	obj := mustHex(t, v.TBS1Hex)
	epochKey := mustHex(t, v.EpochKeyHex)
	epoch := mustEpoch(t, v.Epoch)
	for _, tc := range []struct {
		name          string
		key           []byte
		keyID, handle string
		offset        string
	}{
		{"short epoch key", epochKey[:31], v.KeyID, v.Handle, v.RequestOffset},
		{"short key id", epochKey, v.KeyID[:25], v.Handle, v.RequestOffset},
		{"empty handle", epochKey, v.KeyID, "", v.RequestOffset},
		{"empty offset", epochKey, v.KeyID, v.Handle, ""},
		{"NUL in offset", epochKey, v.KeyID, v.Handle, "2\x00"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if got, err := decryptShapeObject(obj, tc.key, tc.handle, tc.offset, epoch, tc.keyID); err == nil || got != nil {
				t.Fatalf("got (%d bytes, %v), want rejection", len(got), err)
			}
		})
	}
}

func loadShapeVectors(t *testing.T) shapeVectors {
	t.Helper()
	raw, err := os.ReadFile("testdata/shape-vectors.json")
	if err != nil {
		t.Fatal(err)
	}
	var v shapeVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatal(err)
	}
	return v
}

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func mustEpoch(t *testing.T, s string) uint64 {
	t.Helper()
	e, err := strconv.ParseUint(s, 10, 64)
	if err != nil {
		t.Fatal(err)
	}
	return e
}

func newShapeTestGCM(t *testing.T, key []byte) cipher.AEAD {
	t.Helper()
	block, err := aes.NewCipher(key)
	if err != nil {
		t.Fatal(err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		t.Fatal(err)
	}
	return gcm
}

// sealShapeFrames splits plaintext into 1 MiB frames, ending with a short or empty last frame.
func sealShapeFrames(gcm cipher.AEAD, header, plaintext []byte) [][]byte {
	var frames [][]byte
	for i := 0; ; i++ {
		n := min(len(plaintext), shapeTBS1FrameMax)
		last := n < shapeTBS1FrameMax
		frames = append(frames, sealShapeFrame(gcm, header, uint64(i), last, plaintext[:n]))
		plaintext = plaintext[n:]
		if last {
			return frames
		}
	}
}

func sealShapeFrame(gcm cipher.AEAD, header []byte, i uint64, last bool, chunk []byte) []byte {
	word := uint32(len(chunk))
	if last {
		word |= shapeTBS1LastBit
	}
	return gcm.Seal(binary.BigEndian.AppendUint32(nil, word), shapeFrameNonce(i), chunk, shapeFrameAAD(header, i, word))
}

func assembleShapeObject(header []byte, frames [][]byte) []byte {
	obj := append([]byte{}, header...)
	for _, f := range frames {
		obj = append(obj, f...)
	}
	return obj
}

// checkShapeObjectSeal compares the whole object and every frame with the vector's recorded values.
func checkShapeObjectSeal(t *testing.T, obj, header []byte, frames []tbs1Frame, size int, sum string) {
	t.Helper()
	if len(obj) != size {
		t.Fatalf("ciphertext_size = %d, want %d", len(obj), size)
	}
	whole := sha256.Sum256(obj)
	if got := hex.EncodeToString(whole[:]); got != sum {
		t.Fatalf("ciphertext_sha256 = %s, want %s", got, sum)
	}
	if !bytes.Equal(obj[:shapeTBS1HeaderLen], header) {
		t.Fatal("header bytes differ from header_hex")
	}
	pos := shapeTBS1HeaderLen
	for i, f := range frames {
		if pos+4 > len(obj) {
			t.Fatalf("frame %d: object ends before its length word", i)
		}
		word := binary.BigEndian.Uint32(obj[pos : pos+4])
		n := int(word & (shapeTBS1LastBit - 1))
		if pos+4+n+shapeTBS1TagLen > len(obj) {
			t.Fatalf("frame %d: object ends inside the frame", i)
		}
		ct := sha256.Sum256(obj[pos+4 : pos+4+n])
		checks := []struct{ name, got, want string }{
			{"length_word_hex", hex.EncodeToString(obj[pos : pos+4]), f.LengthWordHex},
			{"nonce_hex", hex.EncodeToString(shapeFrameNonce(uint64(i))), f.NonceHex},
			{"aad_hex", hex.EncodeToString(shapeFrameAAD(header, uint64(i), word)), f.AADHex},
			{"ciphertext_sha256", hex.EncodeToString(ct[:]), f.CiphertextSHA256},
			{"tag_hex", hex.EncodeToString(obj[pos+4+n : pos+4+n+shapeTBS1TagLen]), f.TagHex},
		}
		for _, c := range checks {
			if c.got != c.want {
				t.Fatalf("frame %d %s = %s, want %s", i, c.name, c.got, c.want)
			}
		}
		pos += 4 + n + shapeTBS1TagLen
	}
	if pos != len(obj) {
		t.Fatalf("%d bytes after the last vector frame", len(obj)-pos)
	}
}
