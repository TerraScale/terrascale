// Package tbep is the terrabase-go client: the Go client library of the
// TerraBase engine protocol (ADR 0028).
//
// The engine serves gRPC over the protobuf contract in
// crates/terrabase-tbep/proto/terrabase/v1/engine.proto; this package
// binds one application identity to one engine listener, attaches the
// identity and idempotency metadata the contract requires, and maps
// engine failures onto typed errors clients switch on — never on
// string-matched details.
package tbep

import (
	"fmt"

	terrabasev1 "git.terrascale.tech/TerraScale/TerraBase/go/tbep/gen/terrabase/v1"
	"google.golang.org/grpc/status"
)

// EngineError is one refused engine operation: the closed error code
// from the wire contract and its bounded detail.
type EngineError struct {
	// Code is the closed terrabase.v1.ErrorCode the engine answered.
	Code terrabasev1.ErrorCode
	// Detail is the engine's bounded human-readable detail.
	Detail string
}

// Error implements the error interface.
func (e *EngineError) Error() string {
	return fmt.Sprintf("tbep: %s: %s", e.Code.String(), e.Detail)
}

// IsRefusal reports whether the error carries one of the given codes.
func (e *EngineError) IsRefusal(codes ...terrabasev1.ErrorCode) bool {
	for _, code := range codes {
		if e.Code == code {
			return true
		}
	}
	return false
}

// FromError maps a gRPC error onto the engine's typed failure when the
// status details carry a TbepError; other errors pass through intact.
func FromError(err error) error {
	if err == nil {
		return nil
	}
	if engine := engineError(err); engine != nil {
		return engine
	}
	return err
}

func engineError(err error) *EngineError {
	cause, ok := status.FromError(err)
	if !ok {
		return nil
	}
	for _, detail := range cause.Details() {
		if tbepError, ok := detail.(*terrabasev1.TbepError); ok {
			return &EngineError{Code: tbepError.GetCode(), Detail: tbepError.GetDetail()}
		}
	}
	return nil
}
