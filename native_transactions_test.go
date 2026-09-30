package sdk

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"strings"
	"testing"
)

func TestNativeProducerAtomicVectors(t *testing.T) {
	data, err := os.ReadFile("contracts/native/atomic_vectors.json")
	if err != nil {
		t.Fatal(err)
	}
	var corpus struct {
		Vectors []struct {
			Name               string
			Scope              DocumentScope
			Request            CommitRequest
			CanonicalEffectHex string `json:"canonical_effect_hex"`
			EffectDigest       Hex32  `json:"effect_digest"`
		}
	}
	if err := json.Unmarshal(data, &corpus); err != nil {
		t.Fatal(err)
	}
	if len(corpus.Vectors) != 3 {
		t.Fatal("producer vector corpus changed")
	}
	for _, vector := range corpus.Vectors {
		t.Run(vector.Name, func(t *testing.T) {
			if err := vector.Request.validate(vector.Scope); err != nil {
				t.Fatal(err)
			}
			encoded, err := CanonicalEffectBytes(vector.Scope, vector.Request.Authority, vector.Request.Writes)
			if err != nil {
				t.Fatal(err)
			}
			if hex.EncodeToString(encoded) != vector.CanonicalEffectHex {
				t.Fatal("canonical bytes differ from actual producer")
			}
			digest, err := CanonicalEffectDigest(vector.Scope, vector.Request.Authority, vector.Request.Writes)
			if err != nil || digest != vector.EffectDigest {
				t.Fatalf("digest differs: %s %v", digest, err)
			}
			request := vector.Request
			request.TransactionID.Nonce[0] ^= 1
			if err := request.validate(vector.Scope); err != nil {
				t.Fatal("identity changed effect digest")
			}
			changed := vector.Scope
			changed.Shard--
			if err := request.validate(changed); err == nil {
				t.Fatal("effect accepted under a different shard")
			}
		})
	}
}

func TestNativeStatusRequiresExactOutcomeAndIdentity(t *testing.T) {
	request, err := NewCommitRequest(nativeTestScope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{nativeTestWrite()})
	if err != nil {
		t.Fatal(err)
	}
	for _, state := range []TransactionState{TransactionUnknown, TransactionPending, TransactionExpired} {
		response := TransactionStatusResponse{Version: 1, State: state, TransactionID: request.TransactionID, Authority: request.Authority, EffectDigest: request.EffectDigest}
		if err := response.validate(nativeTestScope, request.StatusRequest()); err != nil {
			t.Fatal(err)
		}
		if response.Definitive() {
			t.Fatalf("%s proves abort", state)
		}
		response.Outcome = &TransactionOutcome{Error: &TransactionRejection{Code: "precondition_failed"}}
		if err := response.validate(nativeTestScope, request.StatusRequest()); err == nil {
			t.Fatalf("%s retained a rejection", state)
		}
	}
	receipt := nativeTestReceipt(request)
	response := TransactionStatusResponse{Version: 1, State: TransactionCommitted, TransactionID: request.TransactionID, Authority: request.Authority, EffectDigest: request.EffectDigest, Outcome: &TransactionOutcome{Receipt: &receipt}}
	data, err := json.Marshal(response)
	if err != nil {
		t.Fatal(err)
	}
	var decoded TransactionStatusResponse
	if err := decodeNativeJSON(data, &decoded); err != nil {
		t.Fatal(err)
	}
	if err := decoded.validate(nativeTestScope, request.StatusRequest()); err != nil {
		t.Fatal(err)
	}
	ambiguous := strings.Replace(string(data), `"outcome":{`, `"outcome":{"error":null,`, 1)
	if err := decodeNativeJSON([]byte(ambiguous), &decoded); err == nil {
		t.Fatal("null extra outcome variant accepted")
	}
	response.TransactionID.Nonce[0] ^= 1
	if err := response.validate(nativeTestScope, request.StatusRequest()); err == nil {
		t.Fatal("status identity drift accepted")
	}
}

func TestNativePrepareOwnsAndRejectsDuplicateWrites(t *testing.T) {
	writes := []DocumentWrite{nativeTestWrite()}
	request, err := NewCommitRequest(nativeTestScope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, writes)
	if err != nil {
		t.Fatal(err)
	}
	writes[0].Value.Fields[0].Value = BoolValue(false)
	if err := request.validate(nativeTestScope); err != nil {
		t.Fatal("prepared effects borrowed input")
	}
	if _, err := NewCommitRequest(nativeTestScope, nativeTestID, request.Authority, []DocumentWrite{nativeTestWrite(), nativeTestWrite()}); err == nil {
		t.Fatal("duplicate document accepted")
	}
	if (TransactionStatusResponse{State: TransactionExpired}).Definitive() {
		t.Fatal("expired interpreted as aborted")
	}
}
