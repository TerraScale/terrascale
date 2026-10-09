package app

import (
	"fmt"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

const demoLabsOrganizationID = "7N1PZ3BD9WCGBVV4V50V508NRX"

func seedMarioDemoFixture(account *core.Account) {
	const (
		coreProjectID    = "1X1TETYPPH2APBVN5WGY9D3S3G"
		edgeProjectID    = "1411Y0F25R3D1RAZWB7628Q3JH"
		sandboxProjectID = "06RCAG0WTSX0132AWJ5NEK4KR8"
	)

	// Demo projects use only the supported GRU and SFO regions.
	coreProject := newMarioProject(coreProjectID, "core-platform", "SFO", true, demoTime(2026, time.January, 12, 9, 24))
	edgeProject := newMarioProject(edgeProjectID, "edge-analytics", "GRU", false, demoTime(2026, time.February, 3, 14, 2))
	sandboxProject := newMarioProject(sandboxProjectID, "staging-sandbox", "GRU", false, demoTime(2026, time.March, 21, 11, 47))

	corePrimaryID := "0Z793APJ2ECFW2F7P6J9F2RKVY"
	coreSessionsID := "3S7V1R9W8CKJM87P94PBACW79D"
	edgeEventsID := "185JTBEGC768240CCVHG6TGQH4"
	sandboxDBID := "0YF85EHHPE3AVMCTS72XX4RVYA"
	coreProject.Databases[corePrimaryID] = marioPrimaryDatabase(corePrimaryID, coreProjectID)
	coreProject.Databases[coreSessionsID] = core.Database{
		ID: coreSessionsID, ProjectID: coreProjectID, Name: "core-sessions", Status: "ready",
		Region: "SFO", CreatedAt: demoTime(2026, time.January, 14, 10, 2),
		Documents: map[string]map[string][]byte{
			"sessions": {
				"488YCSAW1HEH6XFB1KYKT1YQB4": []byte(`{"userId":"2RXDD6A70VYXH8M0SDAKFWAQT1","startedAt":"2026-09-17T10:24:00Z","status":"active"}`),
				"7KM4NDWC9RDSEHJQ64DRBNMK69": []byte(`{"userId":"5KPC8W3N5TA88HVG4S068X0MD7","startedAt":"2026-09-12T08:10:00Z","status":"expired"}`),
			},
		},
		Schemas: map[string][]byte{
			"sessions": []byte(`{"name":"sessions","fields":[{"name":"userId","type":"string","required":true,"indexed":true},{"name":"startedAt","type":"timestamp","required":true,"indexed":false},{"name":"status","type":"string","required":true,"indexed":false}],"historyRetentionDays":7,"mergePolicy":"last-write-wins"}`),
		},
	}
	edgeProject.Databases[edgeEventsID] = core.Database{
		ID: edgeEventsID, ProjectID: edgeProjectID, Name: "edge-events", Status: "ready",
		Region: "GRU", CreatedAt: demoTime(2026, time.February, 3, 14, 11),
		Documents: map[string]map[string][]byte{
			"events": {
				"12K95R3TTQBAV0C4VSQ4H3H1D1": []byte(`{"type":"checkout.completed","occurredAt":"2026-09-17T14:10:00Z"}`),
				"133XABR9K9YRWKZ26JVK0K7R3R": []byte(`{"type":"session.started","occurredAt":"2026-09-17T14:12:00Z"}`),
			},
		},
		Schemas: map[string][]byte{
			"events": []byte(`{"name":"events","fields":[{"name":"type","type":"string","required":true,"indexed":true},{"name":"occurredAt","type":"timestamp","required":true,"indexed":false}],"historyRetentionDays":7,"mergePolicy":"last-write-wins"}`),
		},
	}
	sandboxProject.Databases[sandboxDBID] = core.Database{
		ID: sandboxDBID, ProjectID: sandboxProjectID, Name: "sandbox", Status: "provisioning",
		Region: "GRU", CreatedAt: demoTime(2026, time.September, 17, 8, 15),
		Documents: make(map[string]map[string][]byte),
	}

	coreProject.APIKeys = marioAPIKeys(coreProjectID, corePrimaryID)
	coreProject.SavedQueries = map[string]core.SavedQuery{
		"39ZEVV1QSZ378459T0K4B2R2WJ": {
			ID: "39ZEVV1QSZ378459T0K4B2R2WJ", Name: "orders-over-500",
			SQL: "SELECT * FROM orders WHERE total > 500", CreatedAt: demoTime(2026, time.September, 16, 10, 20),
		},
		"01RDCAGXABDJ1KBJ6RGJZM0FE3": {
			ID: "01RDCAGXABDJ1KBJ6RGJZM0FE3", Name: "sessions-active-7d",
			SQL: "SELECT id, startedAt FROM sessions WHERE startedAt > now() - 7d", CreatedAt: demoTime(2026, time.September, 14, 16, 44),
		},
	}

	account.Organizations[demoLabsOrganizationID] = core.Organization{
		ID: demoLabsOrganizationID, Name: "TerraScale Labs", Kind: "business", Slug: "terrascale-labs",
		Status: "active", OwnerUserID: account.User.ID, PlanID: core.ProPlanID,
		BillingStatus: "active", PaymentMethodValidated: true,
		Billing: core.Billing{
			Status: "active", PlanKey: "pro", Interval: "monthly", Invoices: marioInvoices(),
			PaymentMethodBrand: "visa", PaymentMethodLastFour: "4242", PaymentMethodExpiry: "08/28",
		},
		Projects: map[string]core.Project{
			coreProjectID: coreProject, edgeProjectID: edgeProject, sandboxProjectID: sandboxProject,
		},
		Members: map[string]core.Member{
			account.User.ID: {
				UserID: account.User.ID, Email: account.User.Email, Name: account.User.Name,
				Role: "owner", Status: "active", CreatedAt: demoTime(2026, time.September, 18, 7, 12),
			},
			"09MMHD518NFARFD5HB841BS8EA": {
				UserID: "09MMHD518NFARFD5HB841BS8EA", Email: "ana@terrascale.tech", Name: "Ana Souza",
				Role: "admin", Status: "active", CreatedAt: demoTime(2026, time.September, 17, 18, 40),
			},
			"2Q9DGMCYH1PHHNXHC03FEZNSME": {
				UserID: "2Q9DGMCYH1PHHNXHC03FEZNSME", Email: "ravi@terrascale.tech", Name: "Ravi Patel",
				Role: "member", Status: "active", CreatedAt: demoTime(2026, time.September, 15, 9, 3),
			},
		},
		Invitations: map[string]core.Invitation{
			"6BVZ3K6NF18GQYWT9YEJFDS2BF": {
				ID: "6BVZ3K6NF18GQYWT9YEJFDS2BF", Email: "jules@contractor.dev", Role: "member",
				Status: "pending", OrganizationID: demoLabsOrganizationID, InviterEmail: account.User.Email,
				CreatedAt: demoTime(2026, time.September, 16, 15, 0),
			},
			"08R1MGH1KJ1EEAWJPEG21CSY22": {
				ID: "08R1MGH1KJ1EEAWJPEG21CSY22", Email: "former@terrascale.tech", Role: "member",
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
				"01K78000000000000000001001": []byte(`{"customerId":"cus_1001","createdAt":"2026-09-17T07:02:00Z","status":"paid","total":725.5}`),
				"01K78000000000000000001002": []byte(`{"customerId":"cus_1002","createdAt":"2026-09-16T12:15:00Z","status":"paid","total":980.0}`),
				"01K78000000000000000001003": []byte(`{"customerId":"cus_1003","createdAt":"2026-09-15T15:44:00Z","status":"open","total":140.0}`),
			},
		},
		Schemas: map[string][]byte{
			"orders": []byte(`{"name":"orders","fields":[{"name":"customerId","type":"string","required":true,"indexed":true},{"name":"status","type":"string","required":true,"indexed":false},{"name":"total","type":"float","required":true,"indexed":false},{"name":"createdAt","type":"timestamp","required":false,"indexed":false}],"historyRetentionDays":7,"mergePolicy":"last-write-wins"}`),
		},
		IndexRecommendations: []core.IndexRecommendation{{
			ID: "2B1WW8AN8VQ2TWKMCFPTEQAGHP", DatabaseID: id, Collection: "orders",
			Fields: []string{"total"}, Reason: "Filter-heavy order lookups use total.", State: "recommended",
		}},
	}
}

func marioAPIKeys(projectID, databaseID string) map[string]core.APIKey {
	return map[string]core.APIKey{
		"1ZMH7QP4P8FHK6VA2FDKKBBCG5": {
			ID: "1ZMH7QP4P8FHK6VA2FDKKBBCG5", Kind: "api-key", ProjectID: projectID, DatabaseID: databaseID,
			Name: "ci-deploy", Prefix: "tsk_demo_seed", LastFour: "DEMO", Scopes: []string{"databases:read", "documents:write"},
			CreatedAt: demoTime(2026, time.February, 10, 12, 0), ExpiresAt: demoTime(2026, time.December, 10, 12, 0),
		},
		"3ZNHR6VGWYWZMVJY6GCRCVZ5V1": {
			ID: "3ZNHR6VGWYWZMVJY6GCRCVZ5V1", Kind: "api-key", ProjectID: projectID,
			Name: "local-dev", Prefix: "tsk_demo_seed", LastFour: "DEMO", Scopes: []string{"databases:read"},
			CreatedAt: demoTime(2026, time.March, 2, 9, 15),
		},
		"6WJRYMX85ATTMSHXJ39CG96YGA": {
			ID: "6WJRYMX85ATTMSHXJ39CG96YGA", Kind: "api-key", ProjectID: projectID,
			Name: "retired-key", Prefix: "tsk_demo_seed", LastFour: "DEMO", Scopes: []string{"*"},
			CreatedAt: demoTime(2025, time.November, 4, 16, 20), ExpiresAt: demoTime(2026, time.May, 4, 16, 20),
			RevokedAt: demoTime(2026, time.May, 1, 8, 0),
		},
		"01JC9Q8A2C4E6G8J0M2N4Q7STV": {
			ID: "01JC9Q8A2C4E6G8J0M2N4Q7STV", Kind: "m2m", ProjectID: projectID,
			Name: "ingest-worker", Prefix: "tsm_demo_seed", LastFour: "DEMO", Scopes: []string{"documents:write"},
			CreatedAt: demoTime(2026, time.February, 18, 8, 0),
		},
		"01JC9R9B3D5F7H9K1M3P5S8TVW": {
			ID: "01JC9R9B3D5F7H9K1M3P5S8TVW", Kind: "m2m", ProjectID: projectID,
			Name: "edge-sync", Prefix: "tsm_demo_seed", LastFour: "DEMO", Scopes: []string{"databases:read"},
			CreatedAt: demoTime(2026, time.March, 5, 13, 30),
		},
	}
}

func marioNotifications() []core.Notification {
	return []core.Notification{
		{ID: "68946KQMGJWXNF2WTSWP370GJ6", OrganizationID: demoLabsOrganizationID, ProjectID: "1X1TETYPPH2APBVN5WGY9D3S3G", Type: "api-key-expiry", Title: "API key expiring soon", Body: "ci-deploy expires in 7 days. Rotate it to avoid breaking the pipeline.", Status: "unread", Severity: "warning", URL: "/api-keys", CreatedAt: demoTime(2026, time.September, 17, 9, 0)},
		{ID: "258GKYJS87W1EJH7BPDK6C3NNF", OrganizationID: demoLabsOrganizationID, Type: "invitation", Title: "Invitation pending", Body: "jules@contractor.dev has not accepted the invitation yet.", Status: "unread", Severity: "info", URL: "/organization?tab=invitations", CreatedAt: demoTime(2026, time.September, 16, 15, 5)},
		{ID: "3M2P9VGAHSCQ1YVCCKYECN54DS", OrganizationID: demoLabsOrganizationID, ProjectID: "1X1TETYPPH2APBVN5WGY9D3S3G", Type: "quota", Title: "Storage at 82%", Body: "core-primary is using 82% of the plan storage quota.", Status: "read", Severity: "warning", URL: "/usage", CreatedAt: demoTime(2026, time.September, 14, 20, 30)},
		{ID: "71W1N22DQNY5B9QSBVA965QT67", OrganizationID: demoLabsOrganizationID, ProjectID: "1X1TETYPPH2APBVN5WGY9D3S3G", Type: "backup", Title: "Backup completed", Body: "core-primary nightly backup finished in 42s.", Status: "read", Severity: "success", URL: "/databases/0Z793APJ2ECFW2F7P6J9F2RKVY/backups", CreatedAt: demoTime(2026, time.September, 14, 3, 12)},
	}
}

func marioAuditEvents() []core.AuditEvent {
	return []core.AuditEvent{
		{ID: "0S3WH091P8NPAFZ2Y5HVHTE8JQ", OrganizationID: demoLabsOrganizationID, ProjectID: "1X1TETYPPH2APBVN5WGY9D3S3G", Actor: "mario@terrascale.tech", Action: "api_key.created", Target: "ci-deploy", TargetType: "api_key", Risk: "low", Source: "dashboard", OccurredAt: demoTime(2026, time.September, 18, 7, 5)},
		{ID: "611MYHE1QDJXYERR85EY869Z5B", OrganizationID: demoLabsOrganizationID, Actor: "ana@terrascale.tech", Action: "member.role_changed", Target: "ravi@terrascale.tech", TargetType: "member", Risk: "medium", Source: "dashboard", OccurredAt: demoTime(2026, time.September, 17, 14, 22)},
		{ID: "5XXTNQMZNMDGT8DZ986C7XZYEC", OrganizationID: demoLabsOrganizationID, ProjectID: "1X1TETYPPH2APBVN5WGY9D3S3G", Actor: "ci-deploy (api key)", Action: "document.batch_upsert", Target: "core-primary/orders", TargetType: "database", Risk: "low", Source: "api", OccurredAt: demoTime(2026, time.September, 17, 6, 41)},
		{ID: "7T7392ARW2ZDPAX02X93X92S81", OrganizationID: demoLabsOrganizationID, ProjectID: "1X1TETYPPH2APBVN5WGY9D3S3G", Actor: "mario@terrascale.tech", Action: "database.deleted", Target: "old-reporting", TargetType: "database", Risk: "high", Source: "dashboard", OccurredAt: demoTime(2026, time.September, 12, 11, 4)},
		{ID: "0AMW61HE983N01PBS2240P39Q8", OrganizationID: demoLabsOrganizationID, Actor: "system", Action: "backup.restore_completed", Target: "core-sessions", TargetType: "database", Risk: "medium", Source: "scheduled", OccurredAt: demoTime(2026, time.September, 10, 2, 48)},
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
