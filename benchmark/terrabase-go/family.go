package terrabase

// This file contains the canonical codec for the opaque payloads carried by
// FamilyRead and FamilyWrite.  The envelope deliberately stays opaque, but
// its payload is a versioned protobuf oneof.  Keep this code in the client so
// callers do not accidentally send a bare operation message (the wire format
// is FamilyRequestPayload/FamilyResponsePayload).

import (
	"math"
	"strings"
	"unicode/utf8"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

const (
	// FamilyRequestCodecV1 is the codec identifier for FamilyRequestPayload.
	FamilyRequestCodecV1 = "terrabase.family-request/1"
	// FamilyResponseCodecV1 is the codec identifier for FamilyResponsePayload.
	FamilyResponseCodecV1 = "terrabase.family-response/1"
	// MaxFamilyPayloadBytes is the encoded payload bound shared by both codecs.
	MaxFamilyPayloadBytes = maxFrameBytes
	maxFamilyListItems    = 256
	maxFamilyValueDepth   = 64
)

// Short aliases mirror the names used by the Rust codec and are convenient
// when filling the outer FamilyRead/FamilyWrite codec field.
const (
	FamilyRequestCodec  = FamilyRequestCodecV1
	FamilyResponseCodec = FamilyResponseCodecV1
)

// EncodeFamilyRequest validates and deterministically encodes one canonical
// family request payload.
func EncodeFamilyRequest(payload *v1.FamilyRequestPayload) ([]byte, error) {
	if err := validateFamilyRequest(payload); err != nil {
		return nil, err
	}
	return marshalFamily(payload)
}

// DecodeFamilyRequest decodes, validates, and canonicality-checks one family
// request payload. Unknown fields and duplicate singular fields are rejected.
func DecodeFamilyRequest(encoded []byte) (*v1.FamilyRequestPayload, error) {
	payload := new(v1.FamilyRequestPayload)
	if err := unmarshalFamily(encoded, payload); err != nil {
		return nil, err
	}
	if err := validateFamilyRequest(payload); err != nil {
		return nil, err
	}
	return payload, nil
}

// EncodeFamilyResponse validates and deterministically encodes one canonical
// family response payload.
func EncodeFamilyResponse(payload *v1.FamilyResponsePayload) ([]byte, error) {
	if err := validateFamilyResponse(payload); err != nil {
		return nil, err
	}
	return marshalFamily(payload)
}

// DecodeFamilyResponse decodes, validates, and canonicality-checks one family
// response payload.
func DecodeFamilyResponse(encoded []byte) (*v1.FamilyResponsePayload, error) {
	payload := new(v1.FamilyResponsePayload)
	if err := unmarshalFamily(encoded, payload); err != nil {
		return nil, err
	}
	if err := validateFamilyResponse(payload); err != nil {
		return nil, err
	}
	return payload, nil
}

// ValidateFamilyRequest validates a payload without encoding it.
func ValidateFamilyRequest(payload *v1.FamilyRequestPayload) error {
	return validateFamilyRequest(payload)
}

// ValidateFamilyResponse validates a payload without encoding it.
func ValidateFamilyResponse(payload *v1.FamilyResponsePayload) error {
	return validateFamilyResponse(payload)
}

func marshalFamily(message proto.Message) ([]byte, error) {
	if message == nil {
		return nil, localError("family payload is nil")
	}
	if hasFamilyUnknown(message.ProtoReflect()) {
		return nil, localError("family payload contains unknown fields")
	}
	options := proto.MarshalOptions{Deterministic: true}
	if size := options.Size(message); size == 0 {
		return nil, localError("family payload has no oneof variant")
	} else if size > MaxFamilyPayloadBytes {
		return nil, localError("family payload exceeds %d bytes", MaxFamilyPayloadBytes)
	}
	encoded, err := options.Marshal(message)
	if err != nil {
		return nil, localError("family payload could not be encoded: %v", err)
	}
	return encoded, nil
}

func unmarshalFamily(encoded []byte, message proto.Message) error {
	if len(encoded) > MaxFamilyPayloadBytes {
		return localError("family payload exceeds %d bytes", MaxFamilyPayloadBytes)
	}
	if err := (proto.UnmarshalOptions{}).Unmarshal(encoded, message); err != nil {
		return localError("family payload is malformed: %v", err)
	}
	if hasFamilyUnknown(message.ProtoReflect()) {
		return localError("family payload contains unknown fields")
	}
	canonical, err := (proto.MarshalOptions{Deterministic: true}).Marshal(message)
	if err != nil || string(canonical) != string(encoded) {
		return localError("family payload is not canonical")
	}
	return nil
}

func hasFamilyUnknown(message protoreflect.Message) bool {
	if len(message.GetUnknown()) != 0 {
		return true
	}
	unknown := false
	message.Range(func(field protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		if field.Kind() != protoreflect.MessageKind && field.Kind() != protoreflect.GroupKind {
			return true
		}
		if field.IsList() {
			list := value.List()
			for index := 0; index < list.Len(); index++ {
				if hasFamilyUnknown(list.Get(index).Message()) {
					unknown = true
					return false
				}
			}
			return true
		}
		child := value.Message()
		if child.IsValid() && hasFamilyUnknown(child) {
			unknown = true
			return false
		}
		return true
	})
	return unknown
}

func familyIdentifier(field, value string) error {
	if value == "" || len(value) > 255 || value != strings.TrimSpace(value) ||
		!utf8.ValidString(value) || hasControl(value) {
		return localError("family field %s is invalid", field)
	}
	return nil
}

func familyDigest(field, value string) error {
	if len(value) != 64 {
		return localError("family field %s must be a 64-character digest", field)
	}
	for _, character := range value {
		if !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')) {
			return localError("family field %s is not a lowercase hexadecimal digest", field)
		}
	}
	return nil
}

func familyList(field string, length int) error {
	if length > maxFamilyListItems {
		return localError("family field %s exceeds %d items", field, maxFamilyListItems)
	}
	return nil
}

func familyExpected(expected *v1.ExpectedVersion) error {
	if expected == nil {
		return nil
	}
	switch discipline := expected.GetDiscipline().(type) {
	case *v1.ExpectedVersion_Absent:
		if discipline.Absent == nil {
			return localError("family field expected has no discipline")
		}
	case *v1.ExpectedVersion_Any:
		if discipline.Any == nil {
			return localError("family field expected has no discipline")
		}
	case *v1.ExpectedVersion_Exact:
		if discipline == nil {
			return localError("family field expected has no discipline")
		}
		// The exact arm is a scalar and zero is a valid version.
	case nil:
		return localError("family field expected has no discipline")
	default:
		return localError("family field expected has an unsupported discipline")
	}
	return nil
}

func familyValue(value *v1.Value, depth int) error {
	if value == nil {
		return localError("family value is missing")
	}
	if depth > maxFamilyValueDepth {
		return localError("family value nesting exceeds %d levels", maxFamilyValueDepth)
	}
	switch kind := value.GetKind().(type) {
	case *v1.Value_Null, *v1.Value_Boolean, *v1.Value_Integer, *v1.Value_Text:
		return nil
	case *v1.Value_Number:
		if math.IsNaN(kind.Number) || math.IsInf(kind.Number, 0) {
			return localError("family value number must be finite")
		}
		return nil
	case *v1.Value_List:
		if kind.List == nil {
			return localError("family value list is missing")
		}
		if err := familyList("value.list", len(kind.List.Items)); err != nil {
			return err
		}
		for index, item := range kind.List.Items {
			if err := familyValue(item, depth+1); err != nil {
				return localError("family value list item %d: %s", index, localDetail(err))
			}
		}
		return nil
	case *v1.Value_Map:
		if kind.Map == nil {
			return localError("family value map is missing")
		}
		if err := familyList("value.map", len(kind.Map.Entries)); err != nil {
			return err
		}
		previous := ""
		for index, entry := range kind.Map.Entries {
			if entry == nil {
				return localError("family value map entry %d is missing", index)
			}
			if err := familyIdentifier("value.map.name", entry.Name); err != nil {
				return err
			}
			if index != 0 && previous >= entry.Name {
				return localError("family value map entries are not strictly sorted")
			}
			previous = entry.Name
			if err := familyValue(entry.Value, depth+1); err != nil {
				return localError("family value map field %q: %s", entry.Name, localDetail(err))
			}
		}
		return nil
	default:
		return localError("family value has no kind")
	}
}

func validateFamilyRequest(payload *v1.FamilyRequestPayload) error {
	if payload == nil || payload.GetOperation() == nil {
		return localError("family request payload has no operation")
	}
	switch operation := payload.GetOperation().(type) {
	case *v1.FamilyRequestPayload_AttachmentRead:
		if operation.AttachmentRead == nil {
			return localError("family attachment read is missing")
		}
		return familyIdentifier("document", operation.AttachmentRead.Document)
	case *v1.FamilyRequestPayload_CounterRead:
		if operation.CounterRead == nil {
			return localError("family counter read is missing")
		}
		value := operation.CounterRead
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		if err := familyList("names", len(value.Names)); err != nil {
			return err
		}
		for _, name := range value.Names {
			if err := familyIdentifier("names", name); err != nil {
				return err
			}
		}
		return nil
	case *v1.FamilyRequestPayload_TimeSeriesRead:
		if operation.TimeSeriesRead == nil {
			return localError("family time-series read is missing")
		}
		value := operation.TimeSeriesRead
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		if err := familyIdentifier("name", value.Name); err != nil {
			return err
		}
		if value.RangeStart != nil && value.RangeEnd != nil && *value.RangeStart > *value.RangeEnd {
			return localError("family time-series range is invalid")
		}
		if value.Limit != nil && (*value.Limit == 0 || *value.Limit > maxFamilyListItems) {
			return localError("family time-series limit is invalid")
		}
		return nil
	case *v1.FamilyRequestPayload_RevisionRead:
		if operation.RevisionRead == nil {
			return localError("family revision read is missing")
		}
		value := operation.RevisionRead
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		if value.Limit == 0 || value.Limit > maxFamilyListItems {
			return localError("family revision limit is invalid")
		}
		return nil
	case *v1.FamilyRequestPayload_CompareExchangeRead:
		if operation.CompareExchangeRead == nil {
			return localError("family compare-exchange read is missing")
		}
		return familyIdentifier("key", operation.CompareExchangeRead.Key)
	case *v1.FamilyRequestPayload_IdentityRead:
		if operation.IdentityRead == nil {
			return localError("family identity read is missing")
		}
		if err := familyList("names", len(operation.IdentityRead.Names)); err != nil {
			return err
		}
		for _, name := range operation.IdentityRead.Names {
			if err := familyIdentifier("names", name); err != nil {
				return err
			}
		}
		return nil
	case *v1.FamilyRequestPayload_LifecycleRead:
		if operation.LifecycleRead == nil {
			return localError("family lifecycle read is missing")
		}
		return familyIdentifier("document", operation.LifecycleRead.Document)
	case *v1.FamilyRequestPayload_ArchivalRead:
		if operation.ArchivalRead == nil {
			return localError("family archival read is missing")
		}
		return familyIdentifier("document", operation.ArchivalRead.Document)
	case *v1.FamilyRequestPayload_AttachmentContentRead:
		if operation.AttachmentContentRead == nil {
			return localError("family attachment content read is missing")
		}
		value := operation.AttachmentContentRead
		if err := familyDigest("digest", value.Digest); err != nil {
			return err
		}
		switch readRange := value.Range.(type) {
		case *v1.FamilyAttachmentContentRead_Bytes:
			if readRange.Bytes == nil {
				return localError("family attachment content byte range is missing")
			}
			if readRange.Bytes.To != nil && *readRange.Bytes.To < readRange.Bytes.From {
				return localError("family attachment content range is invalid")
			}
		case *v1.FamilyAttachmentContentRead_Suffix:
			if readRange.Suffix == 0 {
				return localError("family attachment content suffix is invalid")
			}
		case nil:
		default:
			return localError("family attachment content range is unsupported")
		}
		return nil
	case *v1.FamilyRequestPayload_AttachmentContentWrite:
		if operation.AttachmentContentWrite == nil {
			return localError("family attachment content write is missing")
		}
		value := operation.AttachmentContentWrite
		if value.ExpectedDigest != nil {
			if len(*value.ExpectedDigest) != 64 {
				return localError("family field expected_digest must be 64 bytes")
			}
		}
		if err := familyList("segments", len(value.Segments)); err != nil {
			return err
		}
		total := 0
		for _, segment := range value.Segments {
			if len(segment) > 64*1024 || total > 8*1024*1024-len(segment) {
				return localError("family attachment content segments exceed their size bound")
			}
			total += len(segment)
		}
		return nil
	case *v1.FamilyRequestPayload_AttachmentContentDelete:
		if operation.AttachmentContentDelete == nil {
			return localError("family attachment content delete is missing")
		}
		if len(operation.AttachmentContentDelete.Digest) != 64 {
			return localError("family field digest must be 64 bytes")
		}
		return nil
	case *v1.FamilyRequestPayload_AttachmentWrite:
		if operation.AttachmentWrite == nil {
			return localError("family attachment write is missing")
		}
		value := operation.AttachmentWrite
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		if err := familyExpected(value.Expected); err != nil {
			return err
		}
		switch mutation := value.Mutation.(type) {
		case *v1.FamilyAttachmentWrite_Store:
			if mutation.Store == nil {
				return localError("family attachment store is missing")
			}
			if err := familyIdentifier("name", mutation.Store.Name); err != nil {
				return err
			}
			if err := familyIdentifier("content_type", mutation.Store.ContentType); err != nil {
				return err
			}
			if len(mutation.Store.Hash) != 64 {
				return localError("family attachment hash must be 64 bytes")
			}
		case *v1.FamilyAttachmentWrite_Delete:
			if mutation.Delete == nil {
				return localError("family attachment delete is missing")
			}
			return familyIdentifier("name", mutation.Delete.Name)
		case *v1.FamilyAttachmentWrite_Rename:
			if mutation.Rename == nil {
				return localError("family attachment rename is missing")
			}
			if err := familyIdentifier("from", mutation.Rename.From); err != nil {
				return err
			}
			return familyIdentifier("to", mutation.Rename.To)
		case *v1.FamilyAttachmentWrite_Copy:
			if mutation.Copy == nil {
				return localError("family attachment copy is missing")
			}
			if err := familyIdentifier("name", mutation.Copy.Name); err != nil {
				return err
			}
			if err := familyIdentifier("to_document", mutation.Copy.ToDocument); err != nil {
				return err
			}
			return familyIdentifier("to_name", mutation.Copy.ToName)
		case *v1.FamilyAttachmentWrite_Move:
			if mutation.Move == nil {
				return localError("family attachment move is missing")
			}
			if err := familyIdentifier("name", mutation.Move.Name); err != nil {
				return err
			}
			if err := familyIdentifier("to_document", mutation.Move.ToDocument); err != nil {
				return err
			}
			return familyIdentifier("to_name", mutation.Move.ToName)
		default:
			return localError("family attachment write has no mutation")
		}
		return nil
	case *v1.FamilyRequestPayload_CounterWrite:
		if operation.CounterWrite == nil {
			return localError("family counter write is missing")
		}
		value := operation.CounterWrite
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		if err := familyExpected(value.Expected); err != nil {
			return err
		}
		if err := familyList("operations", len(value.Operations)); err != nil {
			return err
		}
		for _, item := range value.Operations {
			if item == nil {
				return localError("family counter operation is missing")
			}
			switch op := item.Operation.(type) {
			case *v1.FamilyCounterOperation_Increment:
				if op.Increment == nil {
					return localError("family counter increment is missing")
				}
				if err := familyIdentifier("name", op.Increment.Name); err != nil {
					return err
				}
			case *v1.FamilyCounterOperation_Delete:
				if op.Delete == nil {
					return localError("family counter delete is missing")
				}
				if err := familyIdentifier("name", op.Delete.Name); err != nil {
					return err
				}
			default:
				return localError("family counter operation has no variant")
			}
		}
		return nil
	case *v1.FamilyRequestPayload_TimeSeriesWrite:
		if operation.TimeSeriesWrite == nil {
			return localError("family time-series write is missing")
		}
		value := operation.TimeSeriesWrite
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		if err := familyIdentifier("name", value.Name); err != nil {
			return err
		}
		if err := familyExpected(value.Expected); err != nil {
			return err
		}
		if err := familyList("appends", len(value.Appends)); err != nil {
			return err
		}
		if err := familyList("deletes", len(value.Deletes)); err != nil {
			return err
		}
		for _, append := range value.Appends {
			if append == nil || len(append.Values) == 0 || len(append.Values) > maxFamilyListItems {
				return localError("family time-series values are invalid")
			}
			for _, number := range append.Values {
				if math.IsNaN(number) || math.IsInf(number, 0) {
					return localError("family time-series value must be finite")
				}
			}
		}
		for _, deletion := range value.Deletes {
			if deletion == nil {
				return localError("family time-series deletion is missing")
			}
			switch item := deletion.Deletion.(type) {
			case *v1.FamilyTimeSeriesDelete_Timestamp:
			case *v1.FamilyTimeSeriesDelete_Range:
				if item.Range == nil || item.Range.Start > item.Range.End {
					return localError("family time-series range is invalid")
				}
			default:
				return localError("family time-series deletion has no variant")
			}
		}
		return nil
	case *v1.FamilyRequestPayload_RevisionWrite:
		if operation.RevisionWrite == nil {
			return localError("family revision write is missing")
		}
		value := operation.RevisionWrite
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		switch command := value.Command.(type) {
		case *v1.FamilyRevisionWrite_ForceCreate:
			if command.ForceCreate == nil {
				return localError("family revision force-create is missing")
			}
			return familyExpected(command.ForceCreate.Expected)
		case *v1.FamilyRevisionWrite_Revert:
			if command.Revert == nil {
				return localError("family revision revert is missing")
			}
		case *v1.FamilyRevisionWrite_Configure:
			if command.Configure == nil {
				return localError("family revision configure is missing")
			}
		default:
			return localError("family revision write has no command")
		}
		return nil
	case *v1.FamilyRequestPayload_CompareExchangeWrite:
		if operation.CompareExchangeWrite == nil {
			return localError("family compare-exchange write is missing")
		}
		value := operation.CompareExchangeWrite
		if err := familyIdentifier("key", value.Key); err != nil {
			return err
		}
		switch condition := value.Condition.(type) {
		case *v1.FamilyCompareExchangeWrite_PutIfIndex:
			if condition.PutIfIndex == nil {
				return localError("family compare-exchange condition is missing")
			}
			if condition.PutIfIndex.Index == 0 {
				return localError("family compare-exchange index is invalid")
			}
			return familyValue(condition.PutIfIndex.Value, 0)
		case *v1.FamilyCompareExchangeWrite_PutIfMissing:
			if condition.PutIfMissing == nil {
				return localError("family compare-exchange condition is missing")
			}
			return familyValue(condition.PutIfMissing.Value, 0)
		case *v1.FamilyCompareExchangeWrite_DeleteIfIndex:
			if condition.DeleteIfIndex == nil || condition.DeleteIfIndex.Index == 0 {
				return localError("family compare-exchange index is invalid")
			}
		default:
			return localError("family compare-exchange write has no condition")
		}
		return nil
	case *v1.FamilyRequestPayload_IdentityWrite:
		if operation.IdentityWrite == nil {
			return localError("family identity write is missing")
		}
		switch command := operation.IdentityWrite.Command.(type) {
		case *v1.FamilyIdentityWrite_Allocate:
			if command.Allocate == nil {
				return localError("family identity allocate is missing")
			}
			if err := familyIdentifier("name", command.Allocate.Name); err != nil {
				return err
			}
			if command.Allocate.Count == 0 {
				return localError("family identity count is invalid")
			}
		case *v1.FamilyIdentityWrite_Seed:
			if command.Seed == nil {
				return localError("family identity seed is missing")
			}
			return familyIdentifier("name", command.Seed.Name)
		default:
			return localError("family identity write has no command")
		}
		return nil
	case *v1.FamilyRequestPayload_LifecycleWrite:
		if operation.LifecycleWrite == nil {
			return localError("family lifecycle write is missing")
		}
		value := operation.LifecycleWrite
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		switch command := value.Command.(type) {
		case *v1.FamilyLifecycleWrite_Refresh:
			if command.Refresh == nil {
				return localError("family lifecycle refresh is missing")
			}
		case *v1.FamilyLifecycleWrite_Delete:
			if command.Delete == nil {
				return localError("family lifecycle delete is missing")
			}
		default:
			return localError("family lifecycle write has no command")
		}
		return nil
	case *v1.FamilyRequestPayload_ArchivalWrite:
		if operation.ArchivalWrite == nil {
			return localError("family archival write is missing")
		}
		value := operation.ArchivalWrite
		if err := familyIdentifier("document", value.Document); err != nil {
			return err
		}
		switch command := value.Command.(type) {
		case *v1.FamilyArchivalWrite_Set:
			if command.Set == nil {
				return localError("family archival set is missing")
			}
		case *v1.FamilyArchivalWrite_Delete:
			if command.Delete == nil {
				return localError("family archival delete is missing")
			}
		default:
			return localError("family archival write has no command")
		}
		return nil
	default:
		return localError("family request payload has an unsupported operation")
	}
}

func validateFamilyResponse(payload *v1.FamilyResponsePayload) error {
	if payload == nil || payload.GetResult() == nil {
		return localError("family response payload has no result")
	}
	// Response DTOs intentionally have few required fields: an empty page is
	// a valid answer. Bound repeated data and recursively validate values.
	switch result := payload.GetResult().(type) {
	case *v1.FamilyResponsePayload_Attachment:
		if result.Attachment == nil {
			return localError("family attachment response is missing")
		}
		return familyList("entries", len(result.Attachment.Entries))
	case *v1.FamilyResponsePayload_Counter:
		if result.Counter == nil {
			return localError("family counter response is missing")
		}
		return familyList("entries", len(result.Counter.Entries))
	case *v1.FamilyResponsePayload_TimeSeries:
		if result.TimeSeries == nil {
			return localError("family time-series response is missing")
		}
		if err := familyList("entries", len(result.TimeSeries.Entries)); err != nil {
			return err
		}
		for _, entry := range result.TimeSeries.Entries {
			if entry == nil {
				return localError("family time-series entry is missing")
			}
			if len(entry.Values) > maxFamilyListItems {
				return localError("family time-series values exceed %d items", maxFamilyListItems)
			}
			for _, number := range entry.Values {
				if math.IsNaN(number) || math.IsInf(number, 0) {
					return localError("family time-series value must be finite")
				}
			}
		}
		return nil
	case *v1.FamilyResponsePayload_Revisions:
		if result.Revisions == nil {
			return localError("family revisions response is missing")
		}
		if err := familyList("entries", len(result.Revisions.Entries)); err != nil {
			return err
		}
		for _, entry := range result.Revisions.Entries {
			if entry == nil {
				return localError("family revision entry is missing")
			}
			if entry.Value != nil {
				if err := familyValue(entry.Value, 0); err != nil {
					return err
				}
			}
		}
		return nil
	case *v1.FamilyResponsePayload_CompareExchange:
		if result.CompareExchange == nil {
			return localError("family compare-exchange response is missing")
		}
		if result.CompareExchange.Entry != nil {
			return familyValue(result.CompareExchange.Entry.Value, 0)
		}
		return nil
	case *v1.FamilyResponsePayload_Identities:
		if result.Identities == nil {
			return localError("family identity response is missing")
		}
		return familyList("entries", len(result.Identities.Entries))
	case *v1.FamilyResponsePayload_ExpirationRefresh:
		if result.ExpirationRefresh == nil {
			return localError("family lifecycle response is missing")
		}
		return nil
	case *v1.FamilyResponsePayload_Archival:
		if result.Archival == nil {
			return localError("family archival response is missing")
		}
		return nil
	case *v1.FamilyResponsePayload_Write:
		if result.Write == nil {
			return localError("family write response is missing")
		}
		return nil
	case *v1.FamilyResponsePayload_AttachmentContent:
		if result.AttachmentContent == nil {
			return localError("family attachment content response is missing")
		}
		if err := familyList("chunks", len(result.AttachmentContent.Chunks)); err != nil {
			return err
		}
		return nil
	default:
		return localError("family response payload has an unsupported result")
	}
}

// FamilyRequestFamily returns the family selected by a request payload.
func FamilyRequestFamily(payload *v1.FamilyRequestPayload) (Family, error) {
	if payload == nil || payload.GetOperation() == nil {
		return "", localError("family request payload has no operation")
	}
	switch payload.GetOperation().(type) {
	case *v1.FamilyRequestPayload_AttachmentRead, *v1.FamilyRequestPayload_AttachmentWrite,
		*v1.FamilyRequestPayload_AttachmentContentRead, *v1.FamilyRequestPayload_AttachmentContentWrite,
		*v1.FamilyRequestPayload_AttachmentContentDelete:
		return FamilyAttachment, nil
	case *v1.FamilyRequestPayload_CounterRead, *v1.FamilyRequestPayload_CounterWrite:
		return FamilyCounter, nil
	case *v1.FamilyRequestPayload_TimeSeriesRead, *v1.FamilyRequestPayload_TimeSeriesWrite:
		return FamilyTimeSeries, nil
	case *v1.FamilyRequestPayload_RevisionRead, *v1.FamilyRequestPayload_RevisionWrite:
		return FamilyRevisions, nil
	case *v1.FamilyRequestPayload_CompareExchangeRead, *v1.FamilyRequestPayload_CompareExchangeWrite:
		return FamilyCompareExchange, nil
	case *v1.FamilyRequestPayload_IdentityRead, *v1.FamilyRequestPayload_IdentityWrite:
		return FamilyIdentities, nil
	case *v1.FamilyRequestPayload_LifecycleRead, *v1.FamilyRequestPayload_LifecycleWrite:
		return FamilyExpirationRefresh, nil
	case *v1.FamilyRequestPayload_ArchivalRead, *v1.FamilyRequestPayload_ArchivalWrite:
		return FamilyArchival, nil
	default:
		return "", localError("family request payload has an unsupported operation")
	}
}

// FamilyRequestIsWrite reports whether a request selects a mutation.
func FamilyRequestIsWrite(payload *v1.FamilyRequestPayload) (bool, error) {
	if payload == nil || payload.GetOperation() == nil {
		return false, localError("family request payload has no operation")
	}
	switch payload.GetOperation().(type) {
	case *v1.FamilyRequestPayload_AttachmentWrite, *v1.FamilyRequestPayload_CounterWrite,
		*v1.FamilyRequestPayload_TimeSeriesWrite, *v1.FamilyRequestPayload_RevisionWrite,
		*v1.FamilyRequestPayload_CompareExchangeWrite, *v1.FamilyRequestPayload_IdentityWrite,
		*v1.FamilyRequestPayload_LifecycleWrite, *v1.FamilyRequestPayload_ArchivalWrite,
		*v1.FamilyRequestPayload_AttachmentContentWrite, *v1.FamilyRequestPayload_AttachmentContentDelete:
		return true, nil
	default:
		return false, nil
	}
}

// FamilyResponseFamily returns the family represented by a response. A write
// receipt is family-neutral and returns an empty Family.
func FamilyResponseFamily(payload *v1.FamilyResponsePayload) (Family, error) {
	if payload == nil || payload.GetResult() == nil {
		return "", localError("family response payload has no result")
	}
	switch payload.GetResult().(type) {
	case *v1.FamilyResponsePayload_Attachment:
		return FamilyAttachment, nil
	case *v1.FamilyResponsePayload_AttachmentContent:
		return FamilyAttachment, nil
	case *v1.FamilyResponsePayload_Counter:
		return FamilyCounter, nil
	case *v1.FamilyResponsePayload_TimeSeries:
		return FamilyTimeSeries, nil
	case *v1.FamilyResponsePayload_Revisions:
		return FamilyRevisions, nil
	case *v1.FamilyResponsePayload_CompareExchange:
		return FamilyCompareExchange, nil
	case *v1.FamilyResponsePayload_Identities:
		return FamilyIdentities, nil
	case *v1.FamilyResponsePayload_ExpirationRefresh:
		return FamilyExpirationRefresh, nil
	case *v1.FamilyResponsePayload_Archival:
		return FamilyArchival, nil
	case *v1.FamilyResponsePayload_Write:
		return "", nil
	default:
		return "", localError("family response payload has an unsupported result")
	}
}

// ValidateFamilyRequestEnvelope checks the relationship between the opaque
// outer family/codec fields and a decoded request payload.
func ValidateFamilyRequestEnvelope(family Family, codec string, payload *v1.FamilyRequestPayload) error {
	if err := family.validate(); err != nil {
		return err
	}
	if codec != FamilyRequestCodecV1 {
		return localError("family request codec is %q, expected %q", codec, FamilyRequestCodecV1)
	}
	actual, err := FamilyRequestFamily(payload)
	if err != nil {
		return err
	}
	if actual != family {
		return localError("family request payload is for %q, outer family is %q", actual, family)
	}
	return nil
}

// ValidateFamilyResponseEnvelope checks the relationship between the opaque
// outer family/codec fields and a decoded response payload. Write receipts do
// not carry a family tag and are accepted for any valid family.
func ValidateFamilyResponseEnvelope(family Family, codec string, payload *v1.FamilyResponsePayload) error {
	if err := family.validate(); err != nil {
		return err
	}
	if codec != FamilyResponseCodecV1 {
		return localError("family response codec is %q, expected %q", codec, FamilyResponseCodecV1)
	}
	actual, err := FamilyResponseFamily(payload)
	if err != nil {
		return err
	}
	if actual != "" && actual != family {
		return localError("family response payload is for %q, outer family is %q", actual, family)
	}
	return nil
}

// The typed arm helpers below all wrap the operation in the canonical root
// oneof. They are intentionally small, making it difficult to accidentally
// encode a bare protobuf operation.
func EncodeFamilyAttachmentRead(v *v1.FamilyAttachmentRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_AttachmentRead{AttachmentRead: v}})
}
func EncodeFamilyCounterRead(v *v1.FamilyCounterRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_CounterRead{CounterRead: v}})
}
func EncodeFamilyTimeSeriesRead(v *v1.FamilyTimeSeriesRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_TimeSeriesRead{TimeSeriesRead: v}})
}
func EncodeFamilyRevisionRead(v *v1.FamilyRevisionRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_RevisionRead{RevisionRead: v}})
}
func EncodeFamilyCompareExchangeRead(v *v1.FamilyCompareExchangeRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_CompareExchangeRead{CompareExchangeRead: v}})
}
func EncodeFamilyIdentityRead(v *v1.FamilyIdentityRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_IdentityRead{IdentityRead: v}})
}
func EncodeFamilyLifecycleRead(v *v1.FamilyLifecycleRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_LifecycleRead{LifecycleRead: v}})
}
func EncodeFamilyArchivalRead(v *v1.FamilyArchivalRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_ArchivalRead{ArchivalRead: v}})
}
func EncodeFamilyAttachmentWrite(v *v1.FamilyAttachmentWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_AttachmentWrite{AttachmentWrite: v}})
}
func EncodeFamilyCounterWrite(v *v1.FamilyCounterWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_CounterWrite{CounterWrite: v}})
}
func EncodeFamilyTimeSeriesWrite(v *v1.FamilyTimeSeriesWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_TimeSeriesWrite{TimeSeriesWrite: v}})
}
func EncodeFamilyRevisionWrite(v *v1.FamilyRevisionWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_RevisionWrite{RevisionWrite: v}})
}
func EncodeFamilyCompareExchangeWrite(v *v1.FamilyCompareExchangeWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_CompareExchangeWrite{CompareExchangeWrite: v}})
}
func EncodeFamilyIdentityWrite(v *v1.FamilyIdentityWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_IdentityWrite{IdentityWrite: v}})
}
func EncodeFamilyLifecycleWrite(v *v1.FamilyLifecycleWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_LifecycleWrite{LifecycleWrite: v}})
}
func EncodeFamilyArchivalWrite(v *v1.FamilyArchivalWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_ArchivalWrite{ArchivalWrite: v}})
}
func EncodeFamilyAttachmentContentRead(v *v1.FamilyAttachmentContentRead) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_AttachmentContentRead{AttachmentContentRead: v}})
}
func EncodeFamilyAttachmentContentWrite(v *v1.FamilyAttachmentContentWrite) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_AttachmentContentWrite{AttachmentContentWrite: v}})
}
func EncodeFamilyAttachmentContentDelete(v *v1.FamilyAttachmentContentDelete) ([]byte, error) {
	return EncodeFamilyRequest(&v1.FamilyRequestPayload{Operation: &v1.FamilyRequestPayload_AttachmentContentDelete{AttachmentContentDelete: v}})
}

func EncodeFamilyAttachmentResponse(v *v1.FamilyAttachmentResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_Attachment{Attachment: v}})
}
func EncodeFamilyCounterResponse(v *v1.FamilyCounterResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_Counter{Counter: v}})
}
func EncodeFamilyTimeSeriesResponse(v *v1.FamilyTimeSeriesResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_TimeSeries{TimeSeries: v}})
}
func EncodeFamilyRevisionResponse(v *v1.FamilyRevisionResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_Revisions{Revisions: v}})
}
func EncodeFamilyCompareExchangeResponse(v *v1.FamilyCompareExchangeResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_CompareExchange{CompareExchange: v}})
}
func EncodeFamilyIdentityResponse(v *v1.FamilyIdentityResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_Identities{Identities: v}})
}
func EncodeFamilyLifecycleResponse(v *v1.FamilyLifecycleResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_ExpirationRefresh{ExpirationRefresh: v}})
}
func EncodeFamilyArchivalResponse(v *v1.FamilyArchivalResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_Archival{Archival: v}})
}
func EncodeFamilyWriteReceiptResponse(v *v1.FamilyWriteReceiptResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_Write{Write: v}})
}
func EncodeFamilyAttachmentContentResponse(v *v1.FamilyAttachmentContentResponse) ([]byte, error) {
	return EncodeFamilyResponse(&v1.FamilyResponsePayload{Result: &v1.FamilyResponsePayload_AttachmentContent{AttachmentContent: v}})
}

func DecodeFamilyAttachmentRead(encoded []byte) (*v1.FamilyAttachmentRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_AttachmentRead)
	if !ok || v.AttachmentRead == nil {
		return nil, localError("family payload is not an attachment read")
	}
	return v.AttachmentRead, nil
}
func DecodeFamilyCounterRead(encoded []byte) (*v1.FamilyCounterRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_CounterRead)
	if !ok || v.CounterRead == nil {
		return nil, localError("family payload is not a counter read")
	}
	return v.CounterRead, nil
}
func DecodeFamilyTimeSeriesRead(encoded []byte) (*v1.FamilyTimeSeriesRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_TimeSeriesRead)
	if !ok || v.TimeSeriesRead == nil {
		return nil, localError("family payload is not a time-series read")
	}
	return v.TimeSeriesRead, nil
}
func DecodeFamilyRevisionRead(encoded []byte) (*v1.FamilyRevisionRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_RevisionRead)
	if !ok || v.RevisionRead == nil {
		return nil, localError("family payload is not a revision read")
	}
	return v.RevisionRead, nil
}
func DecodeFamilyCompareExchangeRead(encoded []byte) (*v1.FamilyCompareExchangeRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_CompareExchangeRead)
	if !ok || v.CompareExchangeRead == nil {
		return nil, localError("family payload is not a compare-exchange read")
	}
	return v.CompareExchangeRead, nil
}
func DecodeFamilyIdentityRead(encoded []byte) (*v1.FamilyIdentityRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_IdentityRead)
	if !ok || v.IdentityRead == nil {
		return nil, localError("family payload is not an identity read")
	}
	return v.IdentityRead, nil
}
func DecodeFamilyLifecycleRead(encoded []byte) (*v1.FamilyLifecycleRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_LifecycleRead)
	if !ok || v.LifecycleRead == nil {
		return nil, localError("family payload is not a lifecycle read")
	}
	return v.LifecycleRead, nil
}
func DecodeFamilyArchivalRead(encoded []byte) (*v1.FamilyArchivalRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_ArchivalRead)
	if !ok || v.ArchivalRead == nil {
		return nil, localError("family payload is not an archival read")
	}
	return v.ArchivalRead, nil
}
func DecodeFamilyAttachmentWrite(encoded []byte) (*v1.FamilyAttachmentWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_AttachmentWrite)
	if !ok || v.AttachmentWrite == nil {
		return nil, localError("family payload is not an attachment write")
	}
	return v.AttachmentWrite, nil
}
func DecodeFamilyCounterWrite(encoded []byte) (*v1.FamilyCounterWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_CounterWrite)
	if !ok || v.CounterWrite == nil {
		return nil, localError("family payload is not a counter write")
	}
	return v.CounterWrite, nil
}
func DecodeFamilyTimeSeriesWrite(encoded []byte) (*v1.FamilyTimeSeriesWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_TimeSeriesWrite)
	if !ok || v.TimeSeriesWrite == nil {
		return nil, localError("family payload is not a time-series write")
	}
	return v.TimeSeriesWrite, nil
}
func DecodeFamilyRevisionWrite(encoded []byte) (*v1.FamilyRevisionWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_RevisionWrite)
	if !ok || v.RevisionWrite == nil {
		return nil, localError("family payload is not a revision write")
	}
	return v.RevisionWrite, nil
}
func DecodeFamilyCompareExchangeWrite(encoded []byte) (*v1.FamilyCompareExchangeWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_CompareExchangeWrite)
	if !ok || v.CompareExchangeWrite == nil {
		return nil, localError("family payload is not a compare-exchange write")
	}
	return v.CompareExchangeWrite, nil
}
func DecodeFamilyIdentityWrite(encoded []byte) (*v1.FamilyIdentityWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_IdentityWrite)
	if !ok || v.IdentityWrite == nil {
		return nil, localError("family payload is not an identity write")
	}
	return v.IdentityWrite, nil
}
func DecodeFamilyLifecycleWrite(encoded []byte) (*v1.FamilyLifecycleWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_LifecycleWrite)
	if !ok || v.LifecycleWrite == nil {
		return nil, localError("family payload is not a lifecycle write")
	}
	return v.LifecycleWrite, nil
}
func DecodeFamilyArchivalWrite(encoded []byte) (*v1.FamilyArchivalWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_ArchivalWrite)
	if !ok || v.ArchivalWrite == nil {
		return nil, localError("family payload is not an archival write")
	}
	return v.ArchivalWrite, nil
}
func DecodeFamilyAttachmentContentRead(encoded []byte) (*v1.FamilyAttachmentContentRead, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_AttachmentContentRead)
	if !ok || v.AttachmentContentRead == nil {
		return nil, localError("family payload is not attachment content read")
	}
	return v.AttachmentContentRead, nil
}
func DecodeFamilyAttachmentContentWrite(encoded []byte) (*v1.FamilyAttachmentContentWrite, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_AttachmentContentWrite)
	if !ok || v.AttachmentContentWrite == nil {
		return nil, localError("family payload is not attachment content write")
	}
	return v.AttachmentContentWrite, nil
}
func DecodeFamilyAttachmentContentDelete(encoded []byte) (*v1.FamilyAttachmentContentDelete, error) {
	p, err := DecodeFamilyRequest(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetOperation().(*v1.FamilyRequestPayload_AttachmentContentDelete)
	if !ok || v.AttachmentContentDelete == nil {
		return nil, localError("family payload is not attachment content delete")
	}
	return v.AttachmentContentDelete, nil
}

func DecodeFamilyAttachmentResponse(encoded []byte) (*v1.FamilyAttachmentResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_Attachment)
	if !ok || v.Attachment == nil {
		return nil, localError("family payload is not an attachment response")
	}
	return v.Attachment, nil
}
func DecodeFamilyCounterResponse(encoded []byte) (*v1.FamilyCounterResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_Counter)
	if !ok || v.Counter == nil {
		return nil, localError("family payload is not a counter response")
	}
	return v.Counter, nil
}
func DecodeFamilyTimeSeriesResponse(encoded []byte) (*v1.FamilyTimeSeriesResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_TimeSeries)
	if !ok || v.TimeSeries == nil {
		return nil, localError("family payload is not a time-series response")
	}
	return v.TimeSeries, nil
}
func DecodeFamilyRevisionResponse(encoded []byte) (*v1.FamilyRevisionResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_Revisions)
	if !ok || v.Revisions == nil {
		return nil, localError("family payload is not a revisions response")
	}
	return v.Revisions, nil
}
func DecodeFamilyCompareExchangeResponse(encoded []byte) (*v1.FamilyCompareExchangeResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_CompareExchange)
	if !ok || v.CompareExchange == nil {
		return nil, localError("family payload is not a compare-exchange response")
	}
	return v.CompareExchange, nil
}
func DecodeFamilyIdentityResponse(encoded []byte) (*v1.FamilyIdentityResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_Identities)
	if !ok || v.Identities == nil {
		return nil, localError("family payload is not an identity response")
	}
	return v.Identities, nil
}
func DecodeFamilyLifecycleResponse(encoded []byte) (*v1.FamilyLifecycleResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_ExpirationRefresh)
	if !ok || v.ExpirationRefresh == nil {
		return nil, localError("family payload is not a lifecycle response")
	}
	return v.ExpirationRefresh, nil
}
func DecodeFamilyArchivalResponse(encoded []byte) (*v1.FamilyArchivalResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_Archival)
	if !ok || v.Archival == nil {
		return nil, localError("family payload is not an archival response")
	}
	return v.Archival, nil
}
func DecodeFamilyWriteReceiptResponse(encoded []byte) (*v1.FamilyWriteReceiptResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_Write)
	if !ok || v.Write == nil {
		return nil, localError("family payload is not a write response")
	}
	return v.Write, nil
}
func DecodeFamilyAttachmentContentResponse(encoded []byte) (*v1.FamilyAttachmentContentResponse, error) {
	p, err := DecodeFamilyResponse(encoded)
	if err != nil {
		return nil, err
	}
	v, ok := p.GetResult().(*v1.FamilyResponsePayload_AttachmentContent)
	if !ok || v.AttachmentContent == nil {
		return nil, localError("family payload is not attachment content response")
	}
	return v.AttachmentContent, nil
}
