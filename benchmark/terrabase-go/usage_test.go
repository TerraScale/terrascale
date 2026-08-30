package terrabase

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

func validUsageAdmission(requestID string) UsageAdmissionContext {
	return UsageAdmissionContext{
		Project:          "project-a",
		Database:         "database-a",
		Partition:        "partition-a",
		OriginRegion:     UsageRegionGRU,
		RequestID:        requestID,
		AcceptedAtMillis: 1_788_091_200_000,
		MeterVersion:     1,
		IntegerUnits:     "1",
	}
}

func validUsageRecord(factID string) UsageRecordInput {
	return UsageRecordInput{
		Meter:            UsageRecordPublicEgressBytes,
		Project:          "project-a",
		Database:         "database-a",
		Partition:        "partition-a",
		OriginRegion:     UsageRegionGRU,
		FactID:           factID,
		RecordedAtMillis: 1_788_091_200_000,
		MeterVersion:     1,
		IntegerUnits:     "4096",
	}
}

func TestUsageRecordIsIdempotentAndTyped(t *testing.T) {
	client, fake := startFake(t)
	mustBind(t, client)
	input := validUsageRecord("egress-response-1")

	first, err := client.UsageRecord(context.Background(), input)
	if err != nil {
		t.Fatalf("the first usage record failed: %v", err)
	}
	if first.Sequence == 0 || first.ReplicaEpoch == 0 || first.AlreadyRecorded {
		t.Fatalf("the first usage receipt is wrong: %+v", first)
	}
	if key := fake.lastKey(); key != input.FactID {
		t.Fatalf("the usage fact id must be its idempotency key, got %q", key)
	}

	replay, err := client.UsageRecord(context.Background(), input)
	if err != nil {
		t.Fatalf("the usage replay failed: %v", err)
	}
	if replay.Sequence != first.Sequence || replay.ReplicaEpoch != first.ReplicaEpoch || !replay.AlreadyRecorded {
		t.Fatalf("the usage replay minted a different receipt: first=%+v replay=%+v", first, replay)
	}
}

func TestUsageAdmissionAttachesToEverySupportedMutation(t *testing.T) {
	client, fake := startFake(t)
	mustBind(t, client)
	ctx := context.Background()

	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "doc", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "usage-document-write",
		WithUsageAdmission(validUsageAdmission("usage-document-write"))); err != nil {
		t.Fatalf("the admitted document write failed: %v", err)
	}
	if _, err := client.DocumentDelete(ctx, "books", "doc", ExpectAny(), AckDefault, "usage-document-delete",
		WithUsageAdmission(validUsageAdmission("usage-document-delete"))); err != nil {
		t.Fatalf("the admitted document delete failed: %v", err)
	}
	if _, err := client.DocumentBatch(ctx, []BatchItem{{
		Document:    DocumentPut{Collection: "books", ID: "batch", Body: map[string]Value{"n": 2}},
		Expected:    ExpectAny(),
		OperationID: "batch-item",
	}}, AckDefault,
		WithIdempotencyKey("usage-document-batch"),
		WithUsageAdmission(validUsageAdmission("usage-document-batch"))); err != nil {
		t.Fatalf("the admitted document batch failed: %v", err)
	}
	if _, err := client.FamilyWrite(ctx, FamilyCounter, "hits", "terrabase.family-request/1", []byte("increment"), "usage-family",
		WithUsageAdmission(validUsageAdmission("usage-family"))); err != nil {
		t.Fatalf("the admitted family write failed: %v", err)
	}
	if _, err := client.SyncMutationSubmit(ctx, 1, "sync-doc", SyncPut(map[string]Value{"n": 3}), "usage-sync",
		WithUsageAdmission(validUsageAdmission("usage-sync"))); err != nil {
		t.Fatalf("the admitted sync mutation failed: %v", err)
	}

	fake.mu.Lock()
	admissionCount := len(fake.admissions)
	fake.mu.Unlock()
	if admissionCount != 5 {
		t.Fatalf("the fake observed %d admitted mutations, want 5", admissionCount)
	}
	if last := fake.lastAdmission(); last == nil || last.GetRequestId() != "usage-sync" || last.GetIntegerUnits() != "1" || last.GetOriginRegion() != v1.UsageRegion_USAGE_REGION_GRU {
		t.Fatalf("the last usage admission is wrong: %+v", last)
	}

	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "bad", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "wire-key",
		WithUsageAdmission(validUsageAdmission("different-key"))); !errors.Is(err, ErrIdempotencyPairing) {
		t.Fatalf("a mismatched admission identity must answer ErrIdempotencyPairing, got %v", err)
	}
	if _, err := client.TierRun(ctx, "tier-operation", WithUsageAdmission(validUsageAdmission("tier-operation"))); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an unsupported admission attachment must fail locally, got %v", err)
	}
}

func TestUsageExportPagesStableSnapshot(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	ctx := context.Background()
	request := UsageServiceExportRequest{
		Window:   &UsageWindow{StartMillis: 1_000, EndMillis: 2_000},
		PageSize: 1,
	}
	first, err := client.UsageExport(ctx, request)
	if err != nil {
		t.Fatalf("the first usage page failed: %v", err)
	}
	if first.SnapshotSequence != 2 || len(first.Facts) != 1 || string(first.Continuation) != "page-2" {
		t.Fatalf("the first usage page is wrong: %+v", first)
	}
	fact := first.Facts[0]
	if fact.Tenant != "tenant-a" || fact.FactID != "fact-1" || fact.Region != UsageRegionGRU || fact.IntegerDelta != "1" || fact.Sequence != 1 {
		t.Fatalf("the first usage fact is wrong: %+v", fact)
	}
	if len(fact.Canonical) <= usageChecksumBytes || !bytes.HasSuffix(fact.Canonical, fact.Checksum[:]) {
		t.Fatalf("the canonical envelope and checksum do not agree: %+v", fact)
	}

	token := append([]byte(nil), first.Continuation...)
	request.Continuation = token
	second, err := client.UsageExport(ctx, request)
	if err != nil {
		t.Fatalf("the second usage page failed: %v", err)
	}
	if second.SnapshotSequence != first.SnapshotSequence || len(second.Facts) != 1 || len(second.Continuation) != 0 || second.Facts[0].Sequence != 2 {
		t.Fatalf("the second usage page is wrong: %+v", second)
	}
	token[0] = 'X'
	if string(first.Continuation) != "page-2" {
		t.Fatal("the client did not copy the opaque continuation")
	}
}

func TestUsageValidationAndTenantIsolation(t *testing.T) {
	client, fake := startFake(t)
	mustBind(t, client)
	ctx := context.Background()

	invalidRecords := []UsageRecordInput{
		{},
		func() UsageRecordInput { value := validUsageRecord("fact"); value.Meter = 99; return value }(),
		func() UsageRecordInput { value := validUsageRecord("fact"); value.OriginRegion = 99; return value }(),
		func() UsageRecordInput { value := validUsageRecord("fact"); value.Project = " project"; return value }(),
		func() UsageRecordInput { value := validUsageRecord("fact"); value.RecordedAtMillis = 0; return value }(),
		func() UsageRecordInput { value := validUsageRecord("fact"); value.MeterVersion = 0; return value }(),
		func() UsageRecordInput { value := validUsageRecord("fact"); value.IntegerUnits = "01"; return value }(),
		func() UsageRecordInput { value := validUsageRecord("fact"); value.IntegerUnits = "0"; return value }(),
		func() UsageRecordInput {
			value := validUsageRecord("fact")
			value.IntegerUnits = "170141183460469231731687303715884105728"
			return value
		}(),
		func() UsageRecordInput {
			value := validUsageRecord("fact")
			value.IntegerUnits = "1099511627777"
			return value
		}(),
		func() UsageRecordInput {
			value := validUsageRecord("fact")
			value.Meter = UsageRecordAdmittedRequestUnits
			value.IntegerUnits = "1000001"
			return value
		}(),
	}
	for index, input := range invalidRecords {
		if _, err := client.UsageRecord(ctx, input); !errors.Is(err, ErrInvalidRequest) {
			t.Fatalf("invalid usage record %d must fail locally, got %v", index, err)
		}
	}
	if _, err := client.UsageExport(ctx, UsageServiceExportRequest{PageSize: 0}); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("a zero usage page size must fail locally, got %v", err)
	}
	if _, err := client.UsageExport(ctx, UsageServiceExportRequest{PageSize: 1, Window: &UsageWindow{StartMillis: 5, EndMillis: 5}}); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an empty usage window must fail locally, got %v", err)
	}
	if _, err := client.UsageExport(ctx, UsageServiceExportRequest{PageSize: 1, Continuation: make([]byte, maxUsageContinuationBytes+1)}); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an oversized usage continuation must fail locally, got %v", err)
	}

	fake.usageTenantOverride = "tenant-b"
	if _, err := client.UsageExport(ctx, UsageServiceExportRequest{PageSize: 1}); !errors.Is(err, ErrEngineFailure) {
		t.Fatalf("a cross-tenant usage fact must fail closed, got %v", err)
	}
}

func TestUsageMeterCapsAcceptTheirExactMaximum(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	ctx := context.Background()

	for _, input := range []UsageRecordInput{
		func() UsageRecordInput {
			value := validUsageRecord("admitted-limit")
			value.Meter = UsageRecordAdmittedRequestUnits
			value.IntegerUnits = maxAdmittedRequestUnits
			return value
		}(),
		func() UsageRecordInput {
			value := validUsageRecord("egress-limit")
			value.Meter = UsageRecordPublicEgressBytes
			value.IntegerUnits = maxPublicEgressBytes
			return value
		}(),
	} {
		if _, err := client.UsageRecord(ctx, input); err != nil {
			t.Fatalf("the %s meter rejected its exact limit: %v", input.Meter, err)
		}
	}

	admission := validUsageAdmission("admission-limit")
	admission.IntegerUnits = maxAdmittedRequestUnits
	if _, err := admission.protoForKey(admission.RequestID); err != nil {
		t.Fatalf("usage admission rejected its exact limit: %v", err)
	}
	admission.IntegerUnits = "1000001"
	if _, err := admission.protoForKey(admission.RequestID); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("usage admission accepted units above its limit: %v", err)
	}
}

func TestUsageExportRejectsReservedAndUnknownVocabulary(t *testing.T) {
	tests := []struct {
		name   string
		region *v1.UsageRegion
		meter  *v1.UsageMeter
	}{
		{name: "unspecified region", region: usageRegionPointer(v1.UsageRegion_USAGE_REGION_UNSPECIFIED)},
		{name: "unknown region", region: usageRegionPointer(v1.UsageRegion(99))},
		{name: "reserved meter", meter: usageMeterPointer(v1.UsageMeter(1))},
		{name: "unknown meter", meter: usageMeterPointer(v1.UsageMeter(99))},
		{name: "mismatched contract", meter: usageMeterPointer(v1.UsageMeter_USAGE_METER_PUBLIC_EGRESS_BYTES)},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			client, fake := startFake(t)
			mustBind(t, client)
			fake.usageRegionOverride = test.region
			fake.usageMeterOverride = test.meter
			if _, err := client.UsageExport(context.Background(), UsageServiceExportRequest{PageSize: 1}); !errors.Is(err, ErrEngineFailure) {
				t.Fatalf("a hostile usage vocabulary must fail closed, got %v", err)
			}
		})
	}
}

func usageRegionPointer(value v1.UsageRegion) *v1.UsageRegion {
	return &value
}

func usageMeterPointer(value v1.UsageMeter) *v1.UsageMeter {
	return &value
}

func TestUsageErrorsMapToClosedSentinels(t *testing.T) {
	client, fake := startFake(t)
	mustBind(t, client)
	ctx := context.Background()

	unavailable := v1.ErrorCode_ERROR_CODE_USAGE_UNAVAILABLE
	fake.failCode = &unavailable
	if _, err := client.UsageExport(ctx, UsageServiceExportRequest{PageSize: 1}); !errors.Is(err, ErrUsageUnavailable) {
		t.Fatalf("usage unavailable did not map to its sentinel: %v", err)
	}

	corrupt := v1.ErrorCode_ERROR_CODE_USAGE_CORRUPT
	fake.failCode = &corrupt
	if _, err := client.UsageRecord(ctx, validUsageRecord("corrupt-fact")); !errors.Is(err, ErrUsageCorrupt) {
		t.Fatalf("usage corrupt did not map to its sentinel: %v", err)
	}
}

func TestCanonicalI128Validation(t *testing.T) {
	for _, value := range []string{"1", "170141183460469231731687303715884105727"} {
		if !canonicalI128(value, true) {
			t.Fatalf("canonical positive i128 %q was refused", value)
		}
	}
	for _, value := range []string{"-1", "-170141183460469231731687303715884105728"} {
		if !canonicalI128(value, false) {
			t.Fatalf("canonical signed i128 %q was refused", value)
		}
	}
	for _, value := range []string{"", "0", "-0", "+1", "01", "-01", strings.Repeat("9", 40), "170141183460469231731687303715884105728", "-170141183460469231731687303715884105729"} {
		if canonicalI128(value, false) {
			t.Fatalf("non-canonical or overflowing i128 %q was accepted", value)
		}
	}
}
