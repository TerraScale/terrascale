package app

import (
	"fmt"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

const demoLabsOrganizationID = "org_01JC9Q5V2N4P6T8R0X2Z4B6D8"

func seedMarioDemoFixture(account *core.Account) {
	const (
		coreProjectID    = "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA"
		edgeProjectID    = "prj_01JC9R2X4P6T8V1Z3B5D7F9HC"
		sandboxProjectID = "prj_01JC9S4Y6R8T0V2X4Z6B8D1FE"
	)

	// The Go region catalog has SFO, FRA, and GRU. These are displayed as the
	// supported US, EU, and South America demo locations; they do not claim a
	// one-to-one mapping from the source fixture's AWS region slugs.
	coreProject := newMarioProject(coreProjectID, "core-platform", "SFO", true, demoTime(2026, time.January, 12, 9, 24))
	edgeProject := newMarioProject(edgeProjectID, "edge-analytics", "FRA", false, demoTime(2026, time.February, 3, 14, 2))
	sandboxProject := newMarioProject(sandboxProjectID, "staging-sandbox", "GRU", false, demoTime(2026, time.March, 21, 11, 47))

	corePrimaryID := "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC"
	coreSessionsID := "db_01JC9R3Z6P8T0V4X6Z8B2D4GE"
	edgeEventsID := "db_01JC9S5A7R9V1X3Z5B7D9F3HI"
	sandboxDBID := "db_01JC9T6B8S0X2Z4B6D8F1H3JK"
	coreProject.Databases[corePrimaryID] = marioPrimaryDatabase(corePrimaryID, coreProjectID)
	coreProject.Databases[coreSessionsID] = core.Database{
		ID: coreSessionsID, ProjectID: coreProjectID, Name: "core-sessions", Status: "ready",
		Region: "SFO", CreatedAt: demoTime(2026, time.January, 14, 10, 2),
		Documents: map[string]map[string][]byte{
			"sessions": {
				"ses_1001": []byte(`{"userId":"usr_1001","startedAt":"2026-09-17T10:24:00Z","status":"active"}`),
				"ses_1002": []byte(`{"userId":"usr_1002","startedAt":"2026-09-12T08:10:00Z","status":"expired"}`),
			},
		},
		Schemas: map[string][]byte{
			"sessions": []byte(`{"type":"object","properties":{"startedAt":{"type":"string","format":"date-time"},"status":{"type":"string"},"userId":{"type":"string"}}}`),
		},
	}
	edgeProject.Databases[edgeEventsID] = core.Database{
		ID: edgeEventsID, ProjectID: edgeProjectID, Name: "edge-events", Status: "ready",
		Region: "FRA", CreatedAt: demoTime(2026, time.February, 3, 14, 11),
		Documents: map[string]map[string][]byte{
			"events": {
				"evt_1001": []byte(`{"type":"checkout.completed","occurredAt":"2026-09-17T14:10:00Z"}`),
				"evt_1002": []byte(`{"type":"session.started","occurredAt":"2026-09-17T14:12:00Z"}`),
			},
		},
		Schemas: map[string][]byte{
			"events": []byte(`{"type":"object","properties":{"occurredAt":{"type":"string","format":"date-time"},"type":{"type":"string"}}}`),
		},
	}
	sandboxProject.Databases[sandboxDBID] = core.Database{
		ID: sandboxDBID, ProjectID: sandboxProjectID, Name: "sandbox", Status: "provisioning",
		Region: "GRU", CreatedAt: demoTime(2026, time.September, 17, 8, 15),
		Documents: make(map[string]map[string][]byte),
	}

	coreProject.APIKeys = marioAPIKeys(coreProjectID, corePrimaryID)
	coreProject.SavedQueries = map[string]core.SavedQuery{
		"sq_01JC9XN4R6T8V1X4Z7B9D3F6H": {
			ID: "sq_01JC9XN4R6T8V1X4Z7B9D3F6H", Name: "orders-over-500",
			SQL: "SELECT * FROM orders WHERE total > 500", CreatedAt: demoTime(2026, time.September, 16, 10, 20),
		},
		"sq_01JC9XP5S7U9W2Y5A8C1E5G7J": {
			ID: "sq_01JC9XP5S7U9W2Y5A8C1E5G7J", Name: "sessions-active-7d",
			SQL: "SELECT id, startedAt FROM sessions WHERE startedAt > now() - 7d", CreatedAt: demoTime(2026, time.September, 14, 16, 44),
		},
	}

	account.Organizations[demoLabsOrganizationID] = core.Organization{
		ID: demoLabsOrganizationID, Name: "TerraScale Labs", Kind: "business", Slug: "terrascale-labs",
		Status: "active", OwnerUserID: account.User.ID, PlanID: "plan_pro",
		BillingStatus: "active", PaymentMethodValidated: true,
		Billing: core.Billing{
			Status: "active", PlanKey: "pro", Interval: "monthly", Invoices: marioInvoices(),
		},
		Projects: map[string]core.Project{
			coreProjectID: coreProject, edgeProjectID: edgeProject, sandboxProjectID: sandboxProject,
		},
		Members: map[string]core.Member{
			account.User.ID: {
				UserID: account.User.ID, Email: account.User.Email, Name: account.User.Name,
				Role: "owner", Status: "active", CreatedAt: demoTime(2026, time.September, 18, 7, 12),
			},
			"usr_01JC9R8U5P0W3Y5A7C9E2G5IK": {
				UserID: "usr_01JC9R8U5P0W3Y5A7C9E2G5IK", Email: "ana@terrascale.tech", Name: "Ana Souza",
				Role: "admin", Status: "active", CreatedAt: demoTime(2026, time.September, 17, 18, 40),
			},
			"usr_01JC9S9V6Q1X4Z6B8D0F3H6JN": {
				UserID: "usr_01JC9S9V6Q1X4Z6B8D0F3H6JN", Email: "ravi@terrascale.tech", Name: "Ravi Patel",
				Role: "member", Status: "active", CreatedAt: demoTime(2026, time.September, 15, 9, 3),
			},
		},
		Invitations: map[string]core.Invitation{
			"inv_01JC9TAB2S4U6W8Y1B3D5F8HK": {
				ID: "inv_01JC9TAB2S4U6W8Y1B3D5F8HK", Email: "jules@contractor.dev", Role: "member",
				Status: "pending", OrganizationID: demoLabsOrganizationID, InviterEmail: account.User.Email,
				CreatedAt: demoTime(2026, time.September, 16, 15, 0),
			},
			"inv_01JC9TBC3T5V7X9Z2C4E6G9JL": {
				ID: "inv_01JC9TBC3T5V7X9Z2C4E6G9JL", Email: "former@terrascale.tech", Role: "member",
				Status: "expired", OrganizationID: demoLabsOrganizationID, InviterEmail: "ana@terrascale.tech",
				CreatedAt: demoTime(2026, time.July, 2, 11, 30),
			},
		},
		CheckoutSessions: make(map[string]core.CheckoutSession),
	}
	account.ActiveOrganizationID = demoLabsOrganizationID
	account.ActiveProjectID = coreProjectID
	account.OnboardingCompleted = true
	account.OnboardingStep = "complete"
	account.DemoSecurity.MFAEnabled = true
	account.Notifications = marioNotifications()
	account.AuditEvents = marioAuditEvents()
}

func newMarioProject(id, name, region string, isDefault bool, createdAt time.Time) core.Project {
	return core.Project{
		ID: id, OrganizationID: demoLabsOrganizationID, Name: name, Slug: name,
		Region: region, Regions: []string{region}, Status: "active", IsDefault: isDefault, CreatedAt: createdAt,
		Databases: make(map[string]core.Database), APIKeys: make(map[string]core.APIKey),
		SavedQueries: make(map[string]core.SavedQuery), SharedResultConfigs: make(map[string]core.SharedResultConfig),
		BackupPolicies: make(map[string]core.BackupPolicy),
	}
}

func marioPrimaryDatabase(id, projectID string) core.Database {
	return core.Database{
		ID: id, ProjectID: projectID, Name: "core-primary", Status: "ready", Region: "SFO",
		CreatedAt: demoTime(2026, time.January, 12, 9, 31),
		Documents: map[string]map[string][]byte{
			"orders": {
				"ord_1001": []byte(`{"customerId":"cus_1001","createdAt":"2026-09-17T07:02:00Z","status":"paid","total":725.5}`),
				"ord_1002": []byte(`{"customerId":"cus_1002","createdAt":"2026-09-16T12:15:00Z","status":"paid","total":980.0}`),
				"ord_1003": []byte(`{"customerId":"cus_1003","createdAt":"2026-09-15T15:44:00Z","status":"open","total":140.0}`),
			},
		},
		Schemas: map[string][]byte{
			"orders": []byte(`{"type":"object","properties":{"createdAt":{"type":"string","format":"date-time"},"customerId":{"type":"string"},"status":{"type":"string"},"total":{"type":"number"}},"required":["customerId","status","total"]}`),
		},
		IndexRecommendations: []core.IndexRecommendation{{
			ID: "idxrec_01JC9YQ8T0V2X5A8C1E4G7J0N", DatabaseID: id, Collection: "orders",
			Fields: []string{"total"}, Reason: "Filter-heavy order lookups use total.", State: "recommended",
		}},
	}
}

func marioAPIKeys(projectID, databaseID string) map[string]core.APIKey {
	return map[string]core.APIKey{
		"key_01JC9Q9Z6N8P1R4T7V0X3Z6B9": {
			ID: "key_01JC9Q9Z6N8P1R4T7V0X3Z6B9", Kind: "api-key", ProjectID: projectID, DatabaseID: databaseID,
			Name: "ci-deploy", Prefix: "tsk_demo_seed", LastFour: "DEMO", Scopes: []string{"databases:read", "documents:write"},
			CreatedAt: demoTime(2026, time.February, 10, 12, 0), ExpiresAt: demoTime(2026, time.December, 10, 12, 0),
		},
		"key_01JC9R4A8P0T3V6X9Z2C5E8HB": {
			ID: "key_01JC9R4A8P0T3V6X9Z2C5E8HB", Kind: "api-key", ProjectID: projectID,
			Name: "local-dev", Prefix: "tsk_demo_seed", LastFour: "DEMO", Scopes: []string{"databases:read"},
			CreatedAt: demoTime(2026, time.March, 2, 9, 15),
		},
		"key_01JC9S6B9R1X4Z7B0D3F6J9LM": {
			ID: "key_01JC9S6B9R1X4Z7B0D3F6J9LM", Kind: "api-key", ProjectID: projectID,
			Name: "retired-key", Prefix: "tsk_demo_seed", LastFour: "DEMO", Scopes: []string{"*"},
			CreatedAt: demoTime(2025, time.November, 4, 16, 20), ExpiresAt: demoTime(2026, time.May, 4, 16, 20),
			RevokedAt: demoTime(2026, time.May, 1, 8, 0),
		},
		"m2m_01JC9Q8A2C4E6G8J0L2N4Q7ST": {
			ID: "m2m_01JC9Q8A2C4E6G8J0L2N4Q7ST", Kind: "m2m", ProjectID: projectID,
			Name: "ingest-worker", Prefix: "tsm_demo_seed", LastFour: "DEMO", Scopes: []string{"documents:write"},
			CreatedAt: demoTime(2026, time.February, 18, 8, 0),
		},
		"m2m_01JC9R9B3D5F7H9K1M3P5S8UV": {
			ID: "m2m_01JC9R9B3D5F7H9K1M3P5S8UV", Kind: "m2m", ProjectID: projectID,
			Name: "edge-sync", Prefix: "tsm_demo_seed", LastFour: "DEMO", Scopes: []string{"databases:read"},
			CreatedAt: demoTime(2026, time.March, 5, 13, 30),
		},
	}
}

func marioNotifications() []core.Notification {
	return []core.Notification{
		{ID: "ntf_01JC9UBD4U6W8Y0B3D5F7J2NP", OrganizationID: demoLabsOrganizationID, ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", Type: "api-key-expiry", Title: "API key expiring soon", Body: "ci-deploy expires in 7 days. Rotate it to avoid breaking the pipeline.", Status: "unread", Severity: "warning", CreatedAt: demoTime(2026, time.September, 17, 9, 0)},
		{ID: "ntf_01JC9UCE5V7X9Z1C4E6H1M4OQ", OrganizationID: demoLabsOrganizationID, Type: "invitation", Title: "Invitation pending", Body: "jules@contractor.dev has not accepted the invitation yet.", Status: "unread", Severity: "info", CreatedAt: demoTime(2026, time.September, 16, 15, 5)},
		{ID: "ntf_01JC9UDF6W8Y0D2E5F7J2N5PR", OrganizationID: demoLabsOrganizationID, ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", Type: "quota", Title: "Storage at 82%", Body: "core-primary is using 82% of the plan storage quota.", Status: "read", Severity: "warning", CreatedAt: demoTime(2026, time.September, 14, 20, 30)},
		{ID: "ntf_01JC9UEG7X9Z1E3F6G8K3P6ST", OrganizationID: demoLabsOrganizationID, ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", Type: "backup", Title: "Backup completed", Body: "core-primary nightly backup finished in 42s.", Status: "read", Severity: "success", CreatedAt: demoTime(2026, time.September, 14, 3, 12)},
	}
}

func marioAuditEvents() []core.AuditEvent {
	return []core.AuditEvent{
		{ID: "aud_01JC9VFH8Y0A2C5E8H0K4S8WX", OrganizationID: demoLabsOrganizationID, ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", Actor: "mario@terrascale.tech", Action: "api_key.created", Target: "ci-deploy", TargetType: "api_key", Risk: "low", Source: "dashboard", OccurredAt: demoTime(2026, time.September, 18, 7, 5)},
		{ID: "aud_01JC9VGI9Z1B3D6F9I1L5T9YA", OrganizationID: demoLabsOrganizationID, Actor: "ana@terrascale.tech", Action: "member.role_changed", Target: "ravi@terrascale.tech", TargetType: "member", Risk: "medium", Source: "dashboard", OccurredAt: demoTime(2026, time.September, 17, 14, 22)},
		{ID: "aud_01JC9VHJ0B2C4E7G0J2M6U1ZB", OrganizationID: demoLabsOrganizationID, ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", Actor: "ci-deploy (api key)", Action: "document.batch_upsert", Target: "core-primary/orders", TargetType: "database", Risk: "low", Source: "api", OccurredAt: demoTime(2026, time.September, 17, 6, 41)},
		{ID: "aud_01JC9VIK1C3D5F8H1K3N7V3AC", OrganizationID: demoLabsOrganizationID, ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", Actor: "mario@terrascale.tech", Action: "database.deleted", Target: "old-reporting", TargetType: "database", Risk: "high", Source: "dashboard", OccurredAt: demoTime(2026, time.September, 12, 11, 4)},
		{ID: "aud_01JC9VJM2D4E6G9I2L4P8W5BD", OrganizationID: demoLabsOrganizationID, Actor: "system", Action: "backup.restore_completed", Target: "core-sessions", TargetType: "database", Risk: "medium", Source: "scheduled", OccurredAt: demoTime(2026, time.September, 10, 2, 48)},
	}
}

func marioInvoices() []core.Invoice {
	invoices := []core.Invoice{
		{
			ID: "in_01JCA2QG4M6P8S1V3X5Z7B9DF", Number: "in_01JCA2QG4M6P8S1V3X5Z7B9DF", AmountCents: 2900,
			Currency: "USD", Status: "paid", PeriodStart: demoTime(2026, time.August, 1, 0, 0),
			PeriodEnd: demoTime(2026, time.September, 1, 0, 0), CreatedAt: demoTime(2026, time.September, 1, 8, 2),
		},
		{
			ID: "in_01JCA3RH5N7Q9T2W4Y6A8C2EG", Number: "in_01JCA3RH5N7Q9T2W4Y6A8C2EG", AmountCents: 4140,
			Currency: "USD", Status: "open", PeriodStart: demoTime(2026, time.September, 1, 0, 0),
			PeriodEnd: demoTime(2026, time.October, 1, 0, 0), CreatedAt: demoTime(2026, time.October, 1, 8, 0),
		},
	}
	for index := range 12 {
		i := index + 1
		periodEnd := demoTime(2026, time.August, 1, 0, 0).AddDate(0, -i, 0)
		periodStart := periodEnd.AddDate(0, -1, 0)
		id := fmt.Sprintf("in_01JC%04d%02dHIST%02dQ7T9V2X5", periodEnd.Year(), int(periodEnd.Month()), i)
		status := "paid"
		if i == 7 {
			status = "void"
		}
		amount := int64(2900 + i%3*380)
		invoices = append(invoices, core.Invoice{
			ID: id, Number: id, Status: status, AmountCents: amount, Currency: "USD",
			PeriodStart: periodStart, PeriodEnd: periodEnd,
			CreatedAt: periodEnd.Add(8 * time.Hour),
		})
	}
	return invoices
}

func demoTime(year int, month time.Month, day, hour, minute int) time.Time {
	return time.Date(year, month, day, hour, minute, 0, 0, time.UTC)
}
