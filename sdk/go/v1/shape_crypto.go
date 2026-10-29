package v1

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/hkdf"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"strings"
)

// TBS1 is the sealed shape object format (shape-reads.md section 12). The client
// receives the epoch key K_e from the key endpoint and opens one object at a time.
const (
	shapeTBS1Magic      = "TBS1"
	shapeTBS1HeaderLen  = 70
	shapeTBS1KeyIDLen   = 26
	shapeTBS1FrameMax   = 1 << 20
	shapeTBS1TotalMax   = 8 << 20
	shapeTBS1TagLen     = 16
	shapeTBS1LastBit    = 1 << 31
	shapeTBS1LengthMask = shapeTBS1LastBit - 1
)

var errShapeObjectRejected = errors.New("terrabase: shape object rejected")

// decryptShapeObject opens one TBS1 object and returns its plaintext, or applies nothing.
// The caller supplies the binding it derived from its own request (handle and offset).
func decryptShapeObject(ciphertext []byte, epochKey []byte, handle, offset string, epoch uint64, keyID string) ([]byte, error) {
	if len(epochKey) != 32 || len(keyID) != shapeTBS1KeyIDLen || handle == "" || offset == "" || strings.ContainsRune(handle+offset, 0) {
		return nil, errShapeObjectRejected
	}
	if len(ciphertext) < shapeTBS1HeaderLen || string(ciphertext[:4]) != shapeTBS1Magic {
		return nil, errShapeObjectRejected
	}
	header := ciphertext[:shapeTBS1HeaderLen]
	if binary.BigEndian.Uint64(header[4:12]) != epoch || string(header[12:38]) != keyID {
		return nil, errShapeObjectRejected
	}
	key, err := shapeObjectKey(epochKey, header[38:], shapeObjectBinding(handle, offset))
	if err != nil {
		return nil, err
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}

	var plaintext []byte
	pos := shapeTBS1HeaderLen
	for i := uint64(0); ; i++ {
		if len(ciphertext)-pos < 4 {
			return nil, errShapeObjectRejected
		}
		word := binary.BigEndian.Uint32(ciphertext[pos : pos+4])
		last := word&shapeTBS1LastBit != 0
		n := int(word & shapeTBS1LengthMask)
		if n > shapeTBS1FrameMax || (!last && n != shapeTBS1FrameMax) || (last && n == shapeTBS1FrameMax) {
			return nil, errShapeObjectRejected
		}
		if len(plaintext)+n > shapeTBS1TotalMax || len(ciphertext)-pos-4 < n+shapeTBS1TagLen {
			// Total size and truncation both reject the whole object before any tag check.
			return nil, errShapeObjectRejected
		}
		sealed := ciphertext[pos+4 : pos+4+n+shapeTBS1TagLen]
		plaintext, err = gcm.Open(plaintext, shapeFrameNonce(i), sealed, shapeFrameAAD(header, i, word))
		if err != nil {
			return nil, errShapeObjectRejected
		}
		pos += 4 + n + shapeTBS1TagLen
		if last {
			break
		}
	}
	if pos != len(ciphertext) {
		return nil, errShapeObjectRejected
	}
	return plaintext, nil
}

// shapeObjectBinding is handle ‖ 0x00 ‖ offset, the value both K_o and the tags are bound to.
func shapeObjectBinding(handle, offset string) []byte {
	return append(append([]byte(handle), 0), offset...)
}

// shapeObjectKey derives K_o = HKDF(K_e, salt_obj, "TBS1 object" ‖ 0x00 ‖ binding).
func shapeObjectKey(epochKey, salt, binding []byte) ([]byte, error) {
	info := append(append([]byte("TBS1 object"), 0), binding...)
	return hkdf.Key(sha256.New, epochKey, salt, string(info), 32)
}

// shapeFrameNonce is 4 zero bytes ‖ the frame index as uint64 big-endian.
func shapeFrameNonce(i uint64) []byte {
	nonce := make([]byte, 12)
	binary.BigEndian.PutUint64(nonce[4:], i)
	return nonce
}

// shapeFrameAAD is the header ‖ frame index ‖ length word, exactly as sealed.
func shapeFrameAAD(header []byte, i uint64, word uint32) []byte {
	aad := make([]byte, 0, len(header)+12)
	aad = append(aad, header...)
	aad = binary.BigEndian.AppendUint64(aad, i)
	return binary.BigEndian.AppendUint32(aad, word)
}
