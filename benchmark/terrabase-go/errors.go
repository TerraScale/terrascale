package terrabase

import (
	"fmt"
	"strings"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/anypb"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

// Code is the closed TerraBase engine error vocabulary, carried in the
// details of every refused RPC. Clients map codes, never string-match
// details.
type Code uint8

// The closed engine error vocabulary.
const (
	CodeUnspecified Code = iota
	CodeUnsupportedVersion
	CodeMalformedRequest
	CodeUnknownOperation
	CodeDigestMismatch
	CodeIdempotencyPairing
	CodeUnboundChannel
	CodeAlreadyBound
	CodeInvalidRequest
	CodeOperationRefused
	CodeEngineFailure
	CodeSessionRefused
	CodeSubscriptionRefused
	CodeCheckpointRefused
	CodeShuttingDown
	CodeRequestTooLarge
	CodeOverloaded
	CodeProtocolVersionMismatch
	CodeBackupClusterReadOnly
	CodeBackupClusterImmutable
	CodeColdTierUnavailable
	CodeFrontierUnreachable
)

// String names the code as the engine vocabulary spells it.
func (c Code) String() string {
	switch c {
	case CodeUnspecified:
		return "unspecified"
	case CodeUnsupportedVersion:
		return "unsupported_version"
	case CodeMalformedRequest:
		return "malformed_request"
	case CodeUnknownOperation:
		return "unknown_operation"
	case CodeDigestMismatch:
		return "digest_mismatch"
	case CodeIdempotencyPairing:
		return "idempotency_pairing"
	case CodeUnboundChannel:
		return "unbound_channel"
	case CodeAlreadyBound:
		return "already_bound"
	case CodeInvalidRequest:
		return "invalid_request"
	case CodeOperationRefused:
		return "operation_refused"
	case CodeEngineFailure:
		return "engine_failure"
	case CodeSessionRefused:
		return "session_refused"
	case CodeSubscriptionRefused:
		return "subscription_refused"
	case CodeCheckpointRefused:
		return "checkpoint_refused"
	case CodeShuttingDown:
		return "shutting_down"
	case CodeRequestTooLarge:
		return "request_too_large"
	case CodeOverloaded:
		return "overloaded"
	case CodeProtocolVersionMismatch:
		return "protocol_version_mismatch"
	case CodeBackupClusterReadOnly:
		return "backup_cluster_read_only"
	case CodeBackupClusterImmutable:
		return "backup_cluster_immutable"
	case CodeColdTierUnavailable:
		return "cold_tier_unavailable"
	case CodeFrontierUnreachable:
		return "frontier_unreachable"
	default:
		return fmt.Sprintf("unknown(%d)", uint8(c))
	}
}

// The sentinel error values, one per closed engine code. A refused RPC
// whose details carry code X satisfies errors.Is(err, ErrX); the
// sentinel's detail is empty, the concrete error carries the engine's
// bounded detail.
var (
	ErrUnsupportedVersion      = &Error{Code: CodeUnsupportedVersion}
	ErrMalformedRequest        = &Error{Code: CodeMalformedRequest}
	ErrUnknownOperation        = &Error{Code: CodeUnknownOperation}
	ErrDigestMismatch          = &Error{Code: CodeDigestMismatch}
	ErrIdempotencyPairing      = &Error{Code: CodeIdempotencyPairing}
	ErrUnboundChannel          = &Error{Code: CodeUnboundChannel}
	ErrAlreadyBound            = &Error{Code: CodeAlreadyBound}
	ErrInvalidRequest          = &Error{Code: CodeInvalidRequest}
	ErrOperationRefused        = &Error{Code: CodeOperationRefused}
	ErrEngineFailure           = &Error{Code: CodeEngineFailure}
	ErrSessionRefused          = &Error{Code: CodeSessionRefused}
	ErrSubscriptionRefused     = &Error{Code: CodeSubscriptionRefused}
	ErrCheckpointRefused       = &Error{Code: CodeCheckpointRefused}
	ErrShuttingDown            = &Error{Code: CodeShuttingDown}
	ErrRequestTooLarge         = &Error{Code: CodeRequestTooLarge}
	ErrOverloaded              = &Error{Code: CodeOverloaded}
	ErrProtocolVersionMismatch = &Error{Code: CodeProtocolVersionMismatch}
	ErrBackupClusterReadOnly   = &Error{Code: CodeBackupClusterReadOnly}
	ErrBackupClusterImmutable  = &Error{Code: CodeBackupClusterImmutable}
	ErrColdTierUnavailable     = &Error{Code: CodeColdTierUnavailable}
	ErrFrontierUnreachable     = &Error{Code: CodeFrontierUnreachable}
)

// tbepErrorTypeURL is the detail type URL the engine packs into gRPC
// trailing status details.
const tbepErrorTypeURL = "type.googleapis.com/terrabase.v1.TbepError"

// Error is one refused RPC: the closed engine code and its bounded
// detail. Every sentinel above is a *Error carrying only its code, so
// both errors.Is(err, ErrOverloaded) and errors.As(err, &typed) work.
type Error struct {
	// Code is the closed engine code; CodeUnspecified means the engine
	// answered with a status that carried no engine detail.
	Code Code
	// Detail is the engine's bounded human-readable detail (<= 512
	// bytes); it is diagnostic, never to be string-matched.
	Detail string

	grpcCode codes.Code
}

// Error implements error.
func (e *Error) Error() string {
	code := e.Code.String()
	if e.grpcCode != 0 {
		code = fmt.Sprintf("%s (grpc %s)", code, e.grpcCode)
	}
	if e.Detail == "" {
		return "terrabase: " + code
	}
	return "terrabase: " + code + ": " + e.Detail
}

// Is reports whether the target is the sentinel of the same closed
// code, so errors.Is matches the var Err values above.
func (e *Error) Is(target error) bool {
	other, ok := target.(*Error)
	return ok && other.Code == e.Code
}

// GRPCCode is the transport-level status code the engine paired with
// the engine code; zero when the failure was refused locally.
func (e *Error) GRPCCode() codes.Code { return e.grpcCode }

// localError builds the client-side refusal of one request that never
// left the process: the closed invalid_request code with a local detail.
func localError(format string, arguments ...any) *Error {
	return &Error{Code: CodeInvalidRequest, Detail: fmt.Sprintf(format, arguments...)}
}

// interpret maps one gRPC failure into the typed error model. A status
// whose details carry a TbepError becomes *Error; a transport-level
// failure (unavailable, deadline, cancellation) is returned untouched
// so standard errors.Is and context checks keep working.
func interpret(err error) error {
	if err == nil {
		return nil
	}
	failure, ok := status.FromError(err)
	if !ok {
		return err
	}
	tbep := engineDetail(failure)
	if tbep == nil {
		return err
	}
	return &Error{
		Code:     codeFromProto(int32(tbep.GetCode())),
		Detail:   tbep.GetDetail(),
		grpcCode: failure.Code(),
	}
}

// engineDetail extracts the TbepError detail of one status, tolerating
// both a registry-resolved detail and a raw Any.
func engineDetail(failure *status.Status) *v1.TbepError {
	for _, detail := range failure.Details() {
		switch typed := detail.(type) {
		case *v1.TbepError:
			return typed
		case *anypb.Any:
			if !strings.HasSuffix(typed.GetTypeUrl(), "/terrabase.v1.TbepError") {
				continue
			}
			decoded := new(v1.TbepError)
			if proto.Unmarshal(typed.GetValue(), decoded) == nil {
				return decoded
			}
		}
	}
	return nil
}

// codeFromProto maps the protobuf error code onto the closed
// vocabulary, leaving unknown (future) codes unspecified.
func codeFromProto(code int32) Code {
	switch v1.ErrorCode(code) {
	case v1.ErrorCode_ERROR_CODE_UNSUPPORTED_VERSION:
		return CodeUnsupportedVersion
	case v1.ErrorCode_ERROR_CODE_MALFORMED_REQUEST:
		return CodeMalformedRequest
	case v1.ErrorCode_ERROR_CODE_UNKNOWN_OPERATION:
		return CodeUnknownOperation
	case v1.ErrorCode_ERROR_CODE_DIGEST_MISMATCH:
		return CodeDigestMismatch
	case v1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING:
		return CodeIdempotencyPairing
	case v1.ErrorCode_ERROR_CODE_UNBOUND_CHANNEL:
		return CodeUnboundChannel
	case v1.ErrorCode_ERROR_CODE_ALREADY_BOUND:
		return CodeAlreadyBound
	case v1.ErrorCode_ERROR_CODE_INVALID_REQUEST:
		return CodeInvalidRequest
	case v1.ErrorCode_ERROR_CODE_OPERATION_REFUSED:
		return CodeOperationRefused
	case v1.ErrorCode_ERROR_CODE_ENGINE_FAILURE:
		return CodeEngineFailure
	case v1.ErrorCode_ERROR_CODE_SESSION_REFUSED:
		return CodeSessionRefused
	case v1.ErrorCode_ERROR_CODE_SUBSCRIPTION_REFUSED:
		return CodeSubscriptionRefused
	case v1.ErrorCode_ERROR_CODE_CHECKPOINT_REFUSED:
		return CodeCheckpointRefused
	case v1.ErrorCode_ERROR_CODE_SHUTTING_DOWN:
		return CodeShuttingDown
	case v1.ErrorCode_ERROR_CODE_REQUEST_TOO_LARGE:
		return CodeRequestTooLarge
	case v1.ErrorCode_ERROR_CODE_OVERLOADED:
		return CodeOverloaded
	case v1.ErrorCode_ERROR_CODE_PROTOCOL_VERSION_MISMATCH:
		return CodeProtocolVersionMismatch
	case v1.ErrorCode_ERROR_CODE_BACKUP_CLUSTER_READ_ONLY:
		return CodeBackupClusterReadOnly
	case v1.ErrorCode_ERROR_CODE_BACKUP_CLUSTER_IMMUTABLE:
		return CodeBackupClusterImmutable
	case v1.ErrorCode_ERROR_CODE_COLD_TIER_UNAVAILABLE:
		return CodeColdTierUnavailable
	case v1.ErrorCode_ERROR_CODE_FRONTIER_UNREACHABLE:
		return CodeFrontierUnreachable
	default:
		return CodeUnspecified
	}
}
