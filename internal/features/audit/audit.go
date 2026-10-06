// Package audit owns filtered audit history and exports.
package audit

import (
	"bytes"
	"encoding/csv"
	"encoding/json/v2"
	"fmt"
	"net/http"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
)

const pageSize = 10

type row struct {
	Event       core.AuditEvent
	ActorType   string
	ActionLabel string
	Severity    string
	Outcome     string
}

type pageData struct {
	Rows          []row
	Search        string
	Action        string
	Severity      string
	Range         string
	ActionOptions []string
	Critical      int
	Warn          int
	Info          int
	Filtered      int
	Page          int
	PageCount     int
	Previous      int
	Next          int
	HasPrevious   bool
	HasNext       bool
	Retention     int
}

type filters struct {
	Action   string `json:"action"`
	Search   string `json:"search"`
	Range    string `json:"range"`
	Severity string `json:"severity"`
}

// Mount registers audit history and export routes and tools.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{store: deps.Store}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /audit", func(w http.ResponseWriter, r *http.Request) {
		if _, ok := signedIn(deps, w, r); !ok {
			return
		}
		query := r.URL.Query()
		query.Set("tab", "audit")
		http.Redirect(w, r, "/organization?"+query.Encode(), http.StatusSeeOther)
	})

	mux.HandleFunc("GET /audit/export", func(w http.ResponseWriter, r *http.Request) {
		p, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		query := r.URL.Query()
		f := filters{
			Action: query.Get("action"), Search: strings.TrimSpace(query.Get("search")),
			Range: normalizeRange(query.Get("range")), Severity: normalizeSeverity(query.Get("severity")),
		}
		now := time.Now().UTC()
		filtered, err := service.Export(r.Context(), p, f, now)
		if err != nil {
			writeError(w, err)
			return
		}
		filename := fmt.Sprintf("terrascale-audit-%s-%s", filenamePart(p.ProjectID), now.Format("2006-01-02T15-04-05Z"))
		if query.Get("format") == "json" {
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			w.Header().Set("Content-Disposition", "attachment; filename="+filename+".json")
			payload := struct {
				ProjectID  string            `json:"projectId"`
				ExportedAt time.Time         `json:"exportedAt"`
				Count      int               `json:"count"`
				Filters    filters           `json:"filters"`
				Events     []core.AuditEvent `json:"events"`
			}{ProjectID: p.ProjectID, ExportedAt: now, Count: len(filtered), Filters: f, Events: filtered}
			if err := json.MarshalWrite(w, payload); err != nil {
				return
			}
			return
		}
		var output bytes.Buffer
		writer := csv.NewWriter(&output)
		_ = writer.Write([]string{"auditEventId", "occurredAt", "actor", "actorType", "action", "target", "source", "severity", "outcome"})
		for _, event := range filtered {
			_ = writer.Write([]string{
				event.ID, event.OccurredAt.Format(time.RFC3339Nano), event.Actor, actorType(event.Actor),
				event.Action, event.Target, event.Source, severity(event.Risk), outcome(event.Action, event.Target, event.Risk),
			})
		}
		writer.Flush()
		if err := writer.Error(); err != nil {
			http.Error(w, "could not export audit history", http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "text/csv; charset=utf-8")
		w.Header().Set("Content-Disposition", "attachment; filename="+filename+".csv")
		_, _ = w.Write(output.Bytes())
	})
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, false
	}
	return p, true
}

// OrganizationAudit renders the scoped audit history inside Organization.
func OrganizationAudit(deps core.Deps, wrap func(templ.Component) templ.Component) http.HandlerFunc {
	service := Service{store: deps.Store}
	return func(w http.ResponseWriter, r *http.Request) {
		p, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		query := r.URL.Query()
		data, err := service.Page(r.Context(), p, query.Get("search"), query.Get("action"), query.Get("severity"), query.Get("range"), query.Get("page"), pageSize)
		if err != nil {
			writeError(w, err)
			return
		}
		page := core.Page{
			Layout: "shell", Title: "Organization · TerraScale", Principal: p,
			View: func(core.Page) templ.Component { return wrap(auditContent(data)) },
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render audit history", http.StatusInternalServerError)
		}
	}
}

func scoped(events []core.AuditEvent, p core.Principal) []core.AuditEvent {
	result := make([]core.AuditEvent, 0, len(events))
	for _, event := range events {
		if (event.OrganizationID == "" || event.OrganizationID == p.OrganizationID) &&
			(event.ProjectID == "" || event.ProjectID == p.ProjectID) {
			result = append(result, event)
		}
	}
	slices.SortFunc(result, func(a, b core.AuditEvent) int { return b.OccurredAt.Compare(a.OccurredAt) })
	return result
}

func retained(events []core.AuditEvent, retention int, now time.Time) []core.AuditEvent {
	cutoff := now.Add(-time.Duration(retention) * 24 * time.Hour)
	return slices.DeleteFunc(events, func(event core.AuditEvent) bool {
		return !event.OccurredAt.IsZero() && event.OccurredAt.Before(cutoff)
	})
}

func buildPage(events []core.AuditEvent, retention int, search, action, rawSeverity, rawRange, rawPage string, size int, now time.Time) pageData {
	f := filters{
		Action: normalizeAction(action), Search: strings.TrimSpace(search),
		Range: normalizeRange(rawRange), Severity: normalizeSeverity(rawSeverity),
	}
	filtered := filter(events, f, now, retention)
	options := []string{"all"}
	seen := make(map[string]struct{}, len(events))
	for _, event := range events {
		if _, exists := seen[event.Action]; !exists {
			seen[event.Action] = struct{}{}
			options = append(options, event.Action)
		}
	}
	slices.Sort(options[1:])
	pageCount := max(1, (len(filtered)+size-1)/size)
	page := core.ClampPage(rawPage, pageCount)
	start := (page - 1) * size
	end := min(start+size, len(filtered))
	rows := make([]row, 0, end-start)
	critical, warn, info := 0, 0, 0
	for _, event := range filtered {
		level := severity(event.Risk)
		switch level {
		case "critical":
			critical++
		case "warn":
			warn++
		default:
			info++
		}
	}
	for _, event := range filtered[start:end] {
		rows = append(rows, row{
			Event: event, ActorType: actorType(event.Actor), ActionLabel: actionLabel(event.Action),
			Severity: severity(event.Risk), Outcome: outcome(event.Action, event.Target, event.Risk),
		})
	}
	return pageData{
		Rows: rows, Search: f.Search, Action: f.Action, Severity: f.Severity, Range: f.Range,
		ActionOptions: options, Critical: critical, Warn: warn, Info: info, Filtered: len(filtered),
		Page: page, PageCount: pageCount, Previous: max(1, page-1), Next: min(pageCount, page+1),
		HasPrevious: page > 1, HasNext: page < pageCount, Retention: retention,
	}
}

func filter(events []core.AuditEvent, f filters, now time.Time, retention int) []core.AuditEvent {
	cutoff := now.Add(-time.Duration(min(rangeDays[f.Range], retention)) * 24 * time.Hour)
	search := strings.ToLower(f.Search)
	result := make([]core.AuditEvent, 0, len(events))
	for _, event := range events {
		if f.Action != "all" && f.Action != "" && event.Action != f.Action {
			continue
		}
		if f.Severity != "all" && severity(event.Risk) != f.Severity {
			continue
		}
		if !event.OccurredAt.IsZero() && event.OccurredAt.Before(cutoff) {
			continue
		}
		if f.Search != "" {
			haystack := strings.ToLower(strings.Join([]string{event.Actor, event.Action, event.Target, event.TargetType, event.Source, severity(event.Risk), event.OccurredAt.UTC().Format(time.RFC3339)}, " "))
			if !strings.Contains(haystack, search) {
				continue
			}
		}
		result = append(result, event)
	}
	slices.SortFunc(result, func(a, b core.AuditEvent) int { return b.OccurredAt.Compare(a.OccurredAt) })
	return result
}

func normalizeAction(action string) string {
	if action == "" {
		return "all"
	}
	return action
}

func normalizeRange(value string) string {
	switch value {
	case "24h", "7d", "30d", "90d":
		return value
	default:
		return "7d"
	}
}

func normalizeSeverity(value string) string {
	switch value {
	case "info", "warn", "critical":
		return value
	default:
		return "all"
	}
}

func severity(risk string) string {
	switch strings.ToLower(risk) {
	case "high", "critical":
		return "critical"
	case "medium", "warn", "warning":
		return "warn"
	default:
		return "info"
	}
}

func actorType(actor string) string {
	lower := strings.ToLower(actor)
	if lower == "" || lower == "system" || strings.Contains(lower, "service") || strings.Contains(lower, "bot") || strings.Contains(lower, "m2m") {
		return "service"
	}
	return "user"
}

var (
	rangeDays = map[string]int{"24h": 1, "7d": 7, "30d": 30, "90d": 90}

	actionLabels = map[string]string{
		"api_key.created":          "API key created",
		"member.role_changed":      "Member role changed",
		"document.batch_upsert":    "Documents batch upserted",
		"database.deleted":         "Database deleted",
		"backup.restore_completed": "Backup restore completed",
	}

	actionSeparators = strings.NewReplacer(".", " ", "_", " ")
)

func actionLabel(action string) string {
	if label, ok := actionLabels[action]; ok {
		return label
	}
	words := strings.Fields(actionSeparators.Replace(action))
	for i, word := range words {
		if i == 0 && word != "" {
			words[i] = strings.ToUpper(word[:1]) + word[1:]
		}
	}
	return strings.Join(words, " ")
}

func outcome(action, target, risk string) string {
	haystack := strings.ToLower(action + " " + target + " " + risk)
	if strings.Contains(haystack, "fail") || strings.Contains(haystack, "denied") {
		return "failure"
	}
	return "success"
}

func filenamePart(value string) string {
	if value == "" {
		return "current-project"
	}
	var result strings.Builder
	for _, char := range value {
		if char >= 'a' && char <= 'z' || char >= 'A' && char <= 'Z' || char >= '0' && char <= '9' || char == '-' || char == '_' {
			result.WriteRune(char)
		}
	}
	if result.Len() == 0 {
		return "current-project"
	}
	return result.String()
}

func writeError(w http.ResponseWriter, err error) {
	if !core.WriteAccessError(w, err) {
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
