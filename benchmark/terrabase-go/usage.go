package terrabase

import (
	"bytes"
	"context"
	"fmt"
	"strings"
	"unicode/utf8"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

const (
	maxUsageScopeBytes        = 255
	maxUsageContinuationBytes = 1024
	maxUsageFacts             = 256
	maxUsageCanonicalBytes    = 64 << 10
	usageChecksumBytes        = 32
	maxAdmittedRequestUnits   = "1000000"
	maxPublicEgressBytes      = "1099511627776"
)

// UsageRegion is the closed Region vocabulary accepted by usage accounting.
type UsageRegion uint8

const (
	UsageRegionGRU UsageRegion = iota + 1
	UsageRegionSFO
	UsageRegionFRA
)

func (region UsageRegion) proto() (v1.UsageRegion, bool) {
	switch region {
	case UsageRegionGRU:
		return v1.UsageRegion_USAGE_REGION_GRU, true
	case UsageRegionSFO:
		return v1.UsageRegion_USAGE_REGION_SFO, true
	case UsageRegionFRA:
		return v1.UsageRegion_USAGE_REGION_FRA, true
	default:
		return v1.UsageRegion_USAGE_REGION_UNSPECIFIED, false
	}
}

func usageRegionFromProto(region v1.UsageRegion) (UsageRegion, bool) {
	switch region {
	case v1.UsageRegion_USAGE_REGION_GRU:
		return UsageRegionGRU, true
	case v1.UsageRegion_USAGE_REGION_SFO:
		return UsageRegionSFO, true
	case v1.UsageRegion_USAGE_REGION_FRA:
		return UsageRegionFRA, true
	default:
		return 0, false
	}
}

// UsageAdmissionContext attaches trusted admitted-request accounting to the
// same durable mutation. RequestID must equal the mutation's effective
// idempotency key.
type UsageAdmissionContext struct {
	Project          string
	Database         string
	Partition        string
	OriginRegion     UsageRegion
	RequestID        string
	AcceptedAtMillis uint64
	MeterVersion     uint32
	IntegerUnits     string
}

// WithUsageAdmission attaches one admitted-request fact to a supported origin
// mutation. Supported calls are document write, delete, batch, family write,
// and sync mutation submit.
func WithUsageAdmission(admission UsageAdmissionContext) CallOption {
	return func(config *callConfig) {
		copied := admission
		config.usageAdmission = &copied
	}
}

func (admission UsageAdmissionContext) protoForKey(key string) (*v1.UsageAdmissionContext, error) {
	if err := usageIdentifier("project", admission.Project, maxUsageScopeBytes); err != nil {
		return nil, err
	}
	if err := usageIdentifier("database", admission.Database, maxUsageScopeBytes); err != nil {
		return nil, err
	}
	if err := usageIdentifier("partition", admission.Partition, maxUsageScopeBytes); err != nil {
		return nil, err
	}
	region, ok := admission.OriginRegion.proto()
	if !ok {
		return nil, localError("the usage origin region is outside the closed vocabulary")
	}
	if err := usageIdentifier("request id", admission.RequestID, MaxIdempotencyBytes); err != nil {
		return nil, err
	}
	if admission.RequestID != key {
		return nil, &Error{Code: CodeIdempotencyPairing, Detail: "the usage request id differs from the mutation idempotency key"}
	}
	if admission.AcceptedAtMillis == 0 {
		return nil, localError("the usage admission time is zero")
	}
	if admission.MeterVersion == 0 {
		return nil, localError("the usage meter version is zero")
	}
	if !canonicalI128(admission.IntegerUnits, true) {
		return nil, localError("the usage integer units are not a canonical positive i128")
	}
	if !positiveDecimalAtMost(admission.IntegerUnits, maxAdmittedRequestUnits) {
		return nil, localError("the admitted request units exceed %s", maxAdmittedRequestUnits)
	}
	return &v1.UsageAdmissionContext{
		Project:          admission.Project,
		Database:         admission.Database,
		Partition:        admission.Partition,
		OriginRegion:     region,
		RequestId:        admission.RequestID,
		AcceptedAtMillis: admission.AcceptedAtMillis,
		MeterVersion:     admission.MeterVersion,
		IntegerUnits:     admission.IntegerUnits,
	}, nil
}

// UsageRecordMeter is the closed set accepted by standalone UsageRecord.
type UsageRecordMeter uint8

const (
	UsageRecordAdmittedRequestUnits UsageRecordMeter = iota + 1
	UsageRecordPublicEgressBytes
)

func (meter UsageRecordMeter) proto() (v1.UsageRecordMeter, bool) {
	switch meter {
	case UsageRecordAdmittedRequestUnits:
		return v1.UsageRecordMeter_USAGE_RECORD_METER_ADMITTED_REQUEST_UNITS, true
	case UsageRecordPublicEgressBytes:
		return v1.UsageRecordMeter_USAGE_RECORD_METER_PUBLIC_EGRESS_BYTES, true
	default:
		return v1.UsageRecordMeter_USAGE_RECORD_METER_UNSPECIFIED, false
	}
}

func (meter UsageRecordMeter) maximumUnits() (string, bool) {
	switch meter {
	case UsageRecordAdmittedRequestUnits:
		return maxAdmittedRequestUnits, true
	case UsageRecordPublicEgressBytes:
		return maxPublicEgressBytes, true
	default:
		return "", false
	}
}

// UsageRecordInput is one public-boundary usage fact that cannot attach to an
// engine mutation. FactID is also its idempotency key.
type UsageRecordInput struct {
	Meter            UsageRecordMeter
	Project          string
	Database         string
	Partition        string
	OriginRegion     UsageRegion
	FactID           string
	RecordedAtMillis uint64
	MeterVersion     uint32
	IntegerUnits     string
}

// UsageRecordReceipt is the durable identity of a recorded standalone fact.
type UsageRecordReceipt struct {
	Sequence        uint64
	ReplicaEpoch    uint64
	AlreadyRecorded bool
}

// UsageRecord records one bounded standalone fact. The client pins the
// idempotency metadata to FactID, so replay cannot accidentally mint a second
// identity.
func (c *Client) UsageRecord(ctx context.Context, input UsageRecordInput) (UsageRecordReceipt, error) {
	meter, ok := input.Meter.proto()
	if !ok {
		return UsageRecordReceipt{}, localError("the usage record meter is outside the closed vocabulary")
	}
	region, ok := input.OriginRegion.proto()
	if !ok {
		return UsageRecordReceipt{}, localError("the usage origin region is outside the closed vocabulary")
	}
	for _, field := range []struct {
		name  string
		value string
		limit int
	}{
		{"project", input.Project, maxUsageScopeBytes},
		{"database", input.Database, maxUsageScopeBytes},
		{"partition", input.Partition, maxUsageScopeBytes},
		{"fact id", input.FactID, MaxIdempotencyBytes},
	} {
		if err := usageIdentifier(field.name, field.value, field.limit); err != nil {
			return UsageRecordReceipt{}, err
		}
	}
	if input.RecordedAtMillis == 0 {
		return UsageRecordReceipt{}, localError("the usage recording time is zero")
	}
	if input.MeterVersion == 0 {
		return UsageRecordReceipt{}, localError("the usage meter version is zero")
	}
	if !canonicalI128(input.IntegerUnits, true) {
		return UsageRecordReceipt{}, localError("the usage integer units are not a canonical positive i128")
	}
	maximum, _ := input.Meter.maximumUnits()
	if !positiveDecimalAtMost(input.IntegerUnits, maximum) {
		return UsageRecordReceipt{}, localError("the usage integer units exceed the %s meter limit", input.Meter)
	}
	ctx, err := c.outgoing(ctx, true, input.FactID)
	if err != nil {
		return UsageRecordReceipt{}, err
	}
	response, err := c.usage.Record(ctx, &v1.UsageServiceRecordRequest{
		Meter:            meter,
		Project:          input.Project,
		Database:         input.Database,
		Partition:        input.Partition,
		OriginRegion:     region,
		FactId:           input.FactID,
		RecordedAtMillis: input.RecordedAtMillis,
		MeterVersion:     input.MeterVersion,
		IntegerUnits:     input.IntegerUnits,
	})
	if err != nil {
		return UsageRecordReceipt{}, interpret(err)
	}
	if response == nil || response.GetSequence() == 0 || response.GetReplicaEpoch() == 0 {
		return UsageRecordReceipt{}, contractError("the engine returned an invalid usage record receipt")
	}
	return UsageRecordReceipt{
		Sequence:        response.GetSequence(),
		ReplicaEpoch:    response.GetReplicaEpoch(),
		AlreadyRecorded: response.GetAlreadyRecorded(),
	}, nil
}

// UsageMeter is the authoritative durable meter vocabulary.
type UsageMeter uint8

const (
	UsageMeterAdmittedRequestUnits UsageMeter = iota + 1
	UsageMeterPublicEgressBytes
)

// UsageUnit is the exact integer unit of an exported fact.
type UsageUnit uint8

const (
	UsageUnitRequest UsageUnit = iota + 1
	UsageUnitByte
)

// UsageChargeScope is the scope at which one meter charges.
type UsageChargeScope uint8

const (
	UsageChargeAdmittedRequest UsageChargeScope = iota + 1
	UsageChargePublicEgress
)

// UsageMintingRule names the stable event identity that prevents duplicate
// usage facts.
type UsageMintingRule uint8

const (
	UsageMintStableAdmittedRequest UsageMintingRule = iota + 1
	UsageMintPublicResponse
)

// UsageFact is one WAL-durable canonical accounting fact. IntegerDelta stays
// a string so every signed i128 value is preserved exactly.
type UsageFact struct {
	FactID                 string
	Meter                  UsageMeter
	MeterVersion           uint32
	Unit                   UsageUnit
	ChargeScope            UsageChargeScope
	MintingRule            UsageMintingRule
	Tenant                 string
	Project                string
	Database               string
	Partition              string
	Region                 UsageRegion
	MeasurementStartMillis uint64
	MeasurementEndMillis   uint64
	IntegerDelta           string
	Sequence               uint64
	ReplicaEpoch           uint64
	FormatVersion          uint32
	Canonical              []byte
	Checksum               [usageChecksumBytes]byte
}

// UsageWindow is an inclusive start and exclusive end in Unix milliseconds.
type UsageWindow struct {
	StartMillis uint64
	EndMillis   uint64
}

// UsageServiceExportRequest selects one page of a stable tenant-bound snapshot.
type UsageServiceExportRequest struct {
	Window       *UsageWindow
	PageSize     uint32
	Continuation []byte
}

// UsagePage is one stable-snapshot page. An empty Continuation completes it.
type UsagePage struct {
	Facts            []UsageFact
	Continuation     []byte
	SnapshotSequence uint64
}

// UsageExport reads one page of authoritative usage facts. Existing
// continuation bytes are copied and treated as opaque.
func (c *Client) UsageExport(ctx context.Context, request UsageServiceExportRequest) (UsagePage, error) {
	if request.PageSize == 0 || request.PageSize > maxUsageFacts {
		return UsagePage{}, localError("the usage page size must be 1..=%d", maxUsageFacts)
	}
	if len(request.Continuation) > maxUsageContinuationBytes {
		return UsagePage{}, localError("the usage continuation exceeds %d bytes", maxUsageContinuationBytes)
	}
	wire := &v1.UsageServiceExportRequest{
		PageSize:     request.PageSize,
		Continuation: append([]byte(nil), request.Continuation...),
	}
	if request.Window != nil {
		if request.Window.StartMillis >= request.Window.EndMillis {
			return UsagePage{}, localError("the usage window must be a non-empty half-open interval")
		}
		start, end := request.Window.StartMillis, request.Window.EndMillis
		wire.WindowStartMillis = &start
		wire.WindowEndMillis = &end
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return UsagePage{}, err
	}
	response, err := c.usage.Export(ctx, wire)
	if err != nil {
		return UsagePage{}, interpret(err)
	}
	return c.usagePageFrom(response, request.PageSize)
}

func (c *Client) usagePageFrom(response *v1.UsageServiceExportResponse, pageSize uint32) (UsagePage, error) {
	if response == nil || len(response.GetFacts()) > maxUsageFacts || len(response.GetFacts()) > int(pageSize) {
		return UsagePage{}, contractError("the engine returned an invalid usage page size")
	}
	if len(response.GetContinuation()) > maxUsageContinuationBytes {
		return UsagePage{}, contractError("the engine returned an oversized usage continuation")
	}
	bound := c.bound.Load()
	if bound == nil {
		return UsagePage{}, ErrClientClosed
	}
	page := UsagePage{
		Continuation:     append([]byte(nil), response.GetContinuation()...),
		SnapshotSequence: response.GetSnapshotSequence(),
		Facts:            make([]UsageFact, 0, len(response.GetFacts())),
	}
	var previous *UsageFact
	for _, wire := range response.GetFacts() {
		fact, err := usageFactFrom(wire, bound.scope.Tenant)
		if err != nil {
			return UsagePage{}, err
		}
		if fact.Sequence > page.SnapshotSequence {
			return UsagePage{}, contractError("the engine returned a usage fact beyond the snapshot sequence")
		}
		if previous != nil && !usageFactBefore(*previous, fact) {
			return UsagePage{}, contractError("the engine returned usage facts outside strict canonical order")
		}
		page.Facts = append(page.Facts, fact)
		previous = &page.Facts[len(page.Facts)-1]
	}
	return page, nil
}

func usageFactFrom(wire *v1.UsageFact, tenant string) (UsageFact, error) {
	if wire == nil {
		return UsageFact{}, contractError("the engine returned a nil usage fact")
	}
	for _, field := range []struct {
		name  string
		value string
		limit int
	}{
		{"fact id", wire.GetFactId(), maxUsageScopeBytes},
		{"tenant", wire.GetTenant(), maxTenantBytes},
		{"project", wire.GetProject(), maxUsageScopeBytes},
		{"database", wire.GetDatabase(), maxUsageScopeBytes},
		{"partition", wire.GetPartition(), maxUsageScopeBytes},
	} {
		if err := usageResponseIdentifier(field.name, field.value, field.limit); err != nil {
			return UsageFact{}, err
		}
	}
	if wire.GetTenant() != tenant {
		return UsageFact{}, contractError("the engine returned a usage fact for another tenant")
	}
	region, ok := usageRegionFromProto(wire.GetRegion())
	if !ok {
		return UsageFact{}, contractError("the engine returned an invalid usage region")
	}
	meter, unit, scope, rule, ok := usageContractFrom(wire)
	if !ok {
		return UsageFact{}, contractError("the engine returned an invalid usage meter contract")
	}
	if wire.GetMeterVersion() == 0 || wire.GetSequence() == 0 || wire.GetReplicaEpoch() == 0 || wire.GetFormatVersion() == 0 {
		return UsageFact{}, contractError("the engine returned a usage fact with a zero durable identity")
	}
	if wire.GetMeasurementEndMillis() < wire.GetMeasurementStartMillis() {
		return UsageFact{}, contractError("the engine returned an invalid usage measurement window")
	}
	if !canonicalI128(wire.GetIntegerDelta(), false) {
		return UsageFact{}, contractError("the engine returned a non-canonical usage delta")
	}
	canonical, checksum := wire.GetCanonical(), wire.GetChecksum()
	if len(canonical) > maxUsageCanonicalBytes || len(checksum) != usageChecksumBytes || !bytes.HasSuffix(canonical, checksum) {
		return UsageFact{}, contractError("the engine returned an invalid canonical usage envelope")
	}
	fact := UsageFact{
		FactID:                 wire.GetFactId(),
		Meter:                  meter,
		MeterVersion:           wire.GetMeterVersion(),
		Unit:                   unit,
		ChargeScope:            scope,
		MintingRule:            rule,
		Tenant:                 wire.GetTenant(),
		Project:                wire.GetProject(),
		Database:               wire.GetDatabase(),
		Partition:              wire.GetPartition(),
		Region:                 region,
		MeasurementStartMillis: wire.GetMeasurementStartMillis(),
		MeasurementEndMillis:   wire.GetMeasurementEndMillis(),
		IntegerDelta:           wire.GetIntegerDelta(),
		Sequence:               wire.GetSequence(),
		ReplicaEpoch:           wire.GetReplicaEpoch(),
		FormatVersion:          wire.GetFormatVersion(),
		Canonical:              append([]byte(nil), canonical...),
	}
	copy(fact.Checksum[:], checksum)
	return fact, nil
}

func usageContractFrom(wire *v1.UsageFact) (UsageMeter, UsageUnit, UsageChargeScope, UsageMintingRule, bool) {
	type contract struct {
		meter v1.UsageMeter
		unit  v1.UsageUnit
		scope v1.UsageChargeScope
		rule  v1.UsageMintingRule
	}
	contracts := []struct {
		contract
		publicMeter UsageMeter
		publicUnit  UsageUnit
		publicScope UsageChargeScope
		publicRule  UsageMintingRule
	}{
		{
			contract:    contract{v1.UsageMeter_USAGE_METER_ADMITTED_REQUEST_UNITS, v1.UsageUnit_USAGE_UNIT_REQUEST_UNIT, v1.UsageChargeScope_USAGE_CHARGE_SCOPE_ADMITTED_REQUEST, v1.UsageMintingRule_USAGE_MINTING_RULE_STABLE_ADMITTED_REQUEST},
			publicMeter: UsageMeterAdmittedRequestUnits,
			publicUnit:  UsageUnitRequest,
			publicScope: UsageChargeAdmittedRequest,
			publicRule:  UsageMintStableAdmittedRequest,
		},
		{
			contract:    contract{v1.UsageMeter_USAGE_METER_PUBLIC_EGRESS_BYTES, v1.UsageUnit_USAGE_UNIT_BYTE, v1.UsageChargeScope_USAGE_CHARGE_SCOPE_PUBLIC_EGRESS, v1.UsageMintingRule_USAGE_MINTING_RULE_PUBLIC_RESPONSE},
			publicMeter: UsageMeterPublicEgressBytes,
			publicUnit:  UsageUnitByte,
			publicScope: UsageChargePublicEgress,
			publicRule:  UsageMintPublicResponse,
		},
	}
	for _, expected := range contracts {
		if wire.GetMeter() == expected.meter && wire.GetUnit() == expected.unit && wire.GetChargeScope() == expected.scope && wire.GetMintingRule() == expected.rule {
			return expected.publicMeter, expected.publicUnit, expected.publicScope, expected.publicRule, true
		}
	}
	return 0, 0, 0, 0, false
}

func usageFactBefore(left, right UsageFact) bool {
	if left.Sequence != right.Sequence {
		return left.Sequence < right.Sequence
	}
	if left.MintingRule != right.MintingRule {
		return left.MintingRule < right.MintingRule
	}
	return left.FactID < right.FactID
}

func usageIdentifier(field, value string, limit int) error {
	if value == "" || len(value) > limit || !utf8.ValidString(value) || value != strings.TrimSpace(value) || hasControl(value) {
		return localError("the usage %s is outside 1..=%d bytes or is not canonical text", field, limit)
	}
	return nil
}

func usageResponseIdentifier(field, value string, limit int) error {
	if err := usageIdentifier(field, value, limit); err != nil {
		return contractError("the engine returned an invalid usage %s", field)
	}
	return nil
}

func canonicalI128(value string, positiveOnly bool) bool {
	if value == "" || len(value) > 40 {
		return false
	}
	negative := value[0] == '-'
	digits := value
	if negative {
		if positiveOnly {
			return false
		}
		digits = value[1:]
	}
	if digits == "" || digits == "0" || digits[0] == '0' {
		return false
	}
	for index := 0; index < len(digits); index++ {
		if digits[index] < '0' || digits[index] > '9' {
			return false
		}
	}
	maximum := "170141183460469231731687303715884105727"
	if negative {
		maximum = "170141183460469231731687303715884105728"
	}
	return len(digits) < len(maximum) || len(digits) == len(maximum) && digits <= maximum
}

func positiveDecimalAtMost(value, maximum string) bool {
	return len(value) < len(maximum) || len(value) == len(maximum) && value <= maximum
}

func (region UsageRegion) String() string {
	switch region {
	case UsageRegionGRU:
		return "gru"
	case UsageRegionSFO:
		return "sfo"
	case UsageRegionFRA:
		return "fra"
	default:
		return fmt.Sprintf("unknown(%d)", uint8(region))
	}
}

func (meter UsageRecordMeter) String() string {
	switch meter {
	case UsageRecordAdmittedRequestUnits:
		return "admitted_request_units"
	case UsageRecordPublicEgressBytes:
		return "public_egress_bytes"
	default:
		return fmt.Sprintf("unknown(%d)", uint8(meter))
	}
}
