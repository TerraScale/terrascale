// Package logging writes the process logs as record v1: one JSON object per
// line, with the same keys in every region.
package logging

import (
	"context"
	"io"
	"log/slog"

	"terrascale.tech/dashboard/internal/platform/tracing"
)

// Service names this process in every record.
const Service = "ts-dashboard"

// Handler writes record v1 JSON to w, at INFO and above.
func Handler(w io.Writer) slog.Handler {
	return traceHandler{next: slog.NewJSONHandler(w, &slog.HandlerOptions{ReplaceAttr: utcTime})}
}

// Fields are the attributes every record carries. Fly sets the region and the
// machine ID; both are omitted where it does not.
func Fields(region, node string) []any {
	fields := []any{"service", Service}
	if region != "" {
		fields = append(fields, "region", region)
	}
	if node != "" {
		fields = append(fields, "node", node)
	}
	return fields
}

func utcTime(_ []string, attr slog.Attr) slog.Attr {
	if attr.Key == slog.TimeKey && attr.Value.Kind() == slog.KindTime {
		return slog.Time(slog.TimeKey, attr.Value.Time().UTC())
	}
	return attr
}

// traceHandler adds trace_id to records logged with a context that carries a
// trace. It sits before the call's own attributes, after the fields.
type traceHandler struct{ next slog.Handler }

func (h traceHandler) Enabled(ctx context.Context, level slog.Level) bool {
	return h.next.Enabled(ctx, level)
}

func (h traceHandler) Handle(ctx context.Context, record slog.Record) error {
	if traceID, ok := tracing.TraceID(ctx); ok {
		traced := slog.NewRecord(record.Time, record.Level, record.Message, record.PC)
		traced.AddAttrs(slog.String("trace_id", traceID))
		record.Attrs(func(attr slog.Attr) bool {
			traced.AddAttrs(attr)
			return true
		})
		record = traced
	}
	return h.next.Handle(ctx, record)
}

func (h traceHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return traceHandler{next: h.next.WithAttrs(attrs)}
}

func (h traceHandler) WithGroup(name string) slog.Handler {
	return traceHandler{next: h.next.WithGroup(name)}
}
