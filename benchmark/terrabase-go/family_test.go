package terrabase

import (
	"bytes"
	"errors"
	"testing"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

func TestFamilyRequestRoundTripAndClassification(t *testing.T) {
	payload := &v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_CounterRead{
		CounterRead: &v1.FamilyCounterRead{Document: "doc", Names: []string{"a", "b"}},
	}}
	encoded, err := EncodeFamilyRequest(payload)
	if err != nil {
		t.Fatal(err)
	}
	decoded, err := DecodeFamilyRequest(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(encoded, mustEncodeRequest(t, decoded)) {
		t.Fatal("request encoding was not deterministic")
	}
	family, err := FamilyRequestFamily(decoded)
	if err != nil || family != FamilyCounter {
		t.Fatalf("family = %q, err = %v", family, err)
	}
	isWrite, err := FamilyRequestIsWrite(decoded)
	if err != nil || isWrite {
		t.Fatalf("isWrite = %v, err = %v", isWrite, err)
	}
	arm, err := DecodeFamilyCounterRead(encoded)
	if err != nil || arm.Document != "doc" {
		t.Fatalf("arm = %#v, err = %v", arm, err)
	}
}

func TestFamilyResponseRoundTripAndEnvelope(t *testing.T) {
	payload := &v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_Write{
		Write: &v1.FamilyWriteReceiptResponse{Sequence: 4, OperationId: "op"},
	}}
	encoded, err := EncodeFamilyResponse(payload)
	if err != nil {
		t.Fatal(err)
	}
	decoded, err := DecodeFamilyResponse(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if family, err := FamilyResponseFamily(decoded); err != nil || family != "" {
		t.Fatalf("family = %q, err = %v", family, err)
	}
	if err := ValidateFamilyResponseEnvelope(FamilyCounter, FamilyResponseCodec, decoded); err != nil {
		t.Fatal(err)
	}
	if _, err := DecodeFamilyCounterResponse(encoded); err == nil {
		t.Fatal("wrong typed response arm was accepted")
	}
}

func TestFamilyCodecRejectsMalformedAndNonCanonicalPayloads(t *testing.T) {
	if _, err := DecodeFamilyRequest(nil); err == nil {
		t.Fatal("empty payload was accepted")
	}
	// Field 15 is unknown to the current closed request payload.
	if _, err := DecodeFamilyRequest([]byte{0x78, 0x01}); err == nil {
		t.Fatal("unknown field was accepted")
	}
	payload := &v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_AttachmentRead{
		AttachmentRead: &v1.FamilyAttachmentRead{Document: "doc"},
	}}
	encoded, err := EncodeFamilyRequest(payload)
	if err != nil {
		t.Fatal(err)
	}
	// Duplicate oneof fields are not canonical, even when identical.
	duplicate := append(append([]byte(nil), encoded...), encoded...)
	if _, err := DecodeFamilyRequest(duplicate); err == nil {
		t.Fatal("duplicate oneof was accepted")
	}
	if err := ValidateFamilyRequestEnvelope(FamilyCounter, FamilyRequestCodec, payload); err == nil {
		t.Fatal("family mismatch was accepted")
	}
	var typed *Error
	if err := ValidateFamilyRequest(nil); !errors.As(err, &typed) || typed.Code != CodeInvalidRequest {
		t.Fatalf("error = %v", err)
	}
}

func TestFamilyCodecValidatesRecursiveValuesAndLimits(t *testing.T) {
	unsorted := &v1.Value{Kind: &v1.Value_Map{Map: &v1.ValueMap{Entries: []*v1.ValueEntry{
		{Name: "b", Value: &v1.Value{Kind: &v1.Value_Null{Null: v1.NullValue_NULL_VALUE_NULL}}},
		{Name: "a", Value: &v1.Value{Kind: &v1.Value_Null{Null: v1.NullValue_NULL_VALUE_NULL}}},
	}}}}
	payload := &v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_CompareExchangeWrite{
		CompareExchangeWrite: &v1.FamilyCompareExchangeWrite{Key: "key", Condition: &v1.FamilyCompareExchangeWrite_PutIfMissing{
			PutIfMissing: &v1.FamilyCompareExchangePutIfMissing{Value: unsorted},
		}},
	}}
	if _, err := EncodeFamilyRequest(payload); err == nil {
		t.Fatal("unsorted map was accepted")
	}
	tooMany := make([]string, maxFamilyListItems+1)
	for index := range tooMany {
		tooMany[index] = "n"
	}
	read := &v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_CounterRead{
		CounterRead: &v1.FamilyCounterRead{Document: "doc", Names: tooMany},
	}}
	if _, err := EncodeFamilyRequest(read); err == nil {
		t.Fatal("over-limit list was accepted")
	}
}

func TestFamilyAttachmentContentArmsRoundTrip(t *testing.T) {
	digest := "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
	read := &v1.FamilyAttachmentContentRead{Digest: digest, Range: &v1.FamilyAttachmentContentRead_Suffix{Suffix: 4}}
	encoded, err := EncodeFamilyAttachmentContentRead(read)
	if err != nil {
		t.Fatal(err)
	}
	decoded, err := DecodeFamilyAttachmentContentRead(encoded)
	if err != nil || decoded.GetDigest() != digest {
		t.Fatalf("decoded = %#v, err = %v", decoded, err)
	}
	write := &v1.FamilyAttachmentContentWrite{ExpectedDigest: &digest, Segments: [][]byte{[]byte("content")}}
	if _, err := EncodeFamilyAttachmentContentWrite(write); err != nil {
		t.Fatal(err)
	}
	response := &v1.FamilyAttachmentContentResponse{Digest: digest, Chunks: []*v1.FamilyAttachmentContentChunk{{Bytes: []byte("content"), Last: true}}}
	encoded, err = EncodeFamilyAttachmentContentResponse(response)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := DecodeFamilyAttachmentContentResponse(encoded); err != nil {
		t.Fatal(err)
	}
}

func mustEncodeRequest(t *testing.T, payload *v1.FamilyRequestPayload) []byte {
	t.Helper()
	encoded, err := EncodeFamilyRequest(payload)
	if err != nil {
		t.Fatal(err)
	}
	return encoded
}
