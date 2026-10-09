package terrabase

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	v1 "terrascale.tech/sdk/v1"
)

// Queryable contract fields stay at the top level. Data contains one domain
// record, never an entire account or organization aggregate. Keeping domain
// payloads avoids a second, independently maintained copy of the core model.
type coreRecord struct {
	Kind           string          `json:"kind"`
	ID             string          `json:"-"`
	OwnerID        string          `json:"owner_id,omitempty"`
	AccountID      string          `json:"account_id,omitempty"`
	UserID         string          `json:"user_id,omitempty"`
	OrgID          string          `json:"org_id,omitempty"`
	ProjectID      string          `json:"project_id,omitempty"`
	DatabaseID     string          `json:"database_id,omitempty"`
	RecordType     string          `json:"record_type,omitempty"`
	CollectionName string          `json:"collection_name,omitempty"`
	ResourceID     string          `json:"resource_id,omitempty"`
	BackupID       string          `json:"backup_id,omitempty"`
	Position       int             `json:"position,omitempty"`
	Email          string          `json:"email,omitempty"`
	Name           string          `json:"name,omitempty"`
	NameKey        string          `json:"name_key,omitempty"`
	Slug           string          `json:"slug,omitempty"`
	OwnerUserID    string          `json:"owner_user_id,omitempty"`
	PlanID         string          `json:"plan_id,omitempty"`
	Key            string          `json:"key,omitempty"`
	TokenHash      string          `json:"token_hash,omitempty"`
	CustomerID     string          `json:"customer_id,omitempty"`
	SubscriptionID string          `json:"subscription_id,omitempty"`
	Status         string          `json:"status,omitempty"`
	Demo           bool            `json:"demo,omitempty"`
	Read           bool            `json:"read"`
	CreatedAt      time.Time       `json:"created_at,omitzero"`
	RecordedAt     time.Time       `json:"recorded_at,omitzero"`
	ExpiresAt      time.Time       `json:"expires_at,omitzero"`
	ExpiresAtMS    int64           `json:"expires_at_ms,omitempty"`
	PurgeAt        time.Time       `json:"purge_at,omitzero"`
	Rev            uint64          `json:"rev"`
	AuthVersion    uint64          `json:"auth_version"`
	MaxDatabases   int             `json:"max_databases"`
	MaxMembers     int             `json:"max_members"`
	DBCount        int             `json:"db_count"`
	ActiveMembers  int             `json:"active_members"`
	Data           json.RawMessage `json:"data,omitempty"`
}

type coreKey struct{ collection, id string }

func recordKey(collection, key string) coreKey {
	return coreKey{collection, v1.DocumentIDFor(collection, key)}
}

func claimKey(collection string, parts ...string) coreKey {
	key, _ := json.Marshal(parts)
	return recordKey(collection, "claim:"+string(key))
}

func claim(collection, owner string, parts ...string) (coreKey, coreRecord) {
	k := claimKey(collection, parts...)
	return k, coreRecord{Kind: "unique_claim", ID: k.id, OwnerID: owner}
}

// Only the document key carries a record's identity. Domain structs receive it
// when loaded, while reference fields remain in the persisted payload.
func encodeData(data any) (json.RawMessage, error) {
	body, err := json.Marshal(data)
	if err != nil {
		return nil, err
	}
	if len(body) > 0 && body[0] == '{' {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(body, &fields); err != nil {
			return nil, err
		}
		delete(fields, "ID")
		delete(fields, "id")
		return json.Marshal(fields)
	}
	return body, nil
}

func decodeData[T any](r coreRecord) (T, error) {
	var value T
	data := r.Data
	if len(data) > 0 && data[0] == '{' {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(data, &fields); err != nil {
			return value, coreUnavailable(err)
		}
		fields["ID"], _ = json.Marshal(r.ID)
		var err error
		data, err = json.Marshal(fields)
		if err != nil {
			return value, coreUnavailable(err)
		}
	}
	err := v1.Decode(data, &value)
	if err != nil {
		return value, fmt.Errorf("%w: decode %s: %v", core.ErrUnavailable, r.ID, err)
	}
	return value, nil
}

// encodeCore splits every unbounded account/org child into its contract
// collection. Expired invitation slots are released while retaining history.
func encodeCore(state memstore.State, now time.Time) (map[coreKey]coreRecord, error) {
	records := make(map[coreKey]coreRecord)
	add := func(collection, key string, data any, r coreRecord) error {
		r.Kind = "record"
		var err error
		r.Data, err = encodeData(data)
		if err != nil {
			return fmt.Errorf("%w: encode %s: %v", core.ErrInvalid, collection, err)
		}
		k := recordKey(collection, key)
		r.ID = k.id
		if _, exists := records[k]; exists {
			return core.ErrConflict
		}
		records[k] = r
		return nil
	}
	unique := func(collection, owner string, parts ...string) error {
		k, r := claim(collection, owner, parts...)
		if old, exists := records[k]; exists && old.OwnerID != owner {
			return core.ErrConflict
		}
		records[k] = r
		return nil
	}
	for _, a := range state.Accounts {
		fields := core.CloneAccountFields(a)
		fields.AccessTokens, fields.Notifications, fields.AuditEvents = nil, nil, nil
		if err := add("users", a.ID, fields, coreRecord{ID: a.ID, UserID: a.User.ID, Email: core.NormalizeEmail(a.User.Email), Name: a.User.Name, NameKey: strings.ToLower(a.User.Name), AuthVersion: a.AuthVersion, CreatedAt: a.CreatedAt}); err != nil {
			return nil, err
		}
		if err := unique("users", a.ID, "email", core.NormalizeEmail(a.User.Email)); err != nil {
			return nil, err
		}
		if err := unique("users", a.ID, "user_id", a.User.ID); err != nil {
			return nil, err
		}
		if a.User.AuthSubject != "" {
			if err := unique("users", a.ID, "auth_subject", a.User.AuthSubject); err != nil {
				return nil, err
			}
		}
		for _, token := range a.AccessTokens {
			if err := unique("access_tokens", token.ID, "token_hash", token.SecretHash); err != nil {
				return nil, err
			}
			if err := add("access_tokens", token.ID, token, coreRecord{ID: token.ID, AccountID: a.ID, UserID: a.User.ID, TokenHash: token.SecretHash, AuthVersion: a.AuthVersion, ExpiresAt: token.ExpiresAt, CreatedAt: token.CreatedAt}); err != nil {
				return nil, err
			}
		}
		for _, n := range a.Notifications {
			if err := add("notifications", n.ID, n, coreRecord{ID: n.ID, AccountID: a.ID, UserID: a.User.ID, OrgID: n.OrganizationID, ProjectID: n.ProjectID, Read: !n.Unread(), CreatedAt: n.CreatedAt}); err != nil {
				return nil, err
			}
		}
		for _, e := range a.AuditEvents {
			r := coreRecord{ID: e.ID, AccountID: a.ID, UserID: a.User.ID, OrgID: e.OrganizationID, ProjectID: e.ProjectID, CreatedAt: e.OccurredAt}
			if days := state.Organizations[e.OrganizationID].AuditRetentionDays; days > 0 {
				r.PurgeAt = e.OccurredAt.AddDate(0, 0, days)
			}
			if err := add("audit_events", e.ID, e, r); err != nil {
				return nil, err
			}
		}
	}
	for _, org := range state.Organizations {
		fields := org
		fields.Projects, fields.Members, fields.Invitations, fields.CheckoutSessions = nil, nil, nil, nil
		fields.Billing = core.Billing{}
		active := 0
		for _, member := range org.Members {
			if member.Status == "" || strings.EqualFold(member.Status, "active") {
				active++
			}
		}
		if err := add("organizations", org.ID, fields, coreRecord{ID: org.ID, OwnerUserID: org.OwnerUserID, Slug: org.Slug, PlanID: org.PlanID, DBCount: org.DatabaseCount(), ActiveMembers: active, CreatedAt: org.CreatedAt}); err != nil {
			return nil, err
		}
		if org.Slug != "" {
			if err := unique("organizations", org.ID, "slug", org.OwnerUserID, org.Slug); err != nil {
				return nil, err
			}
		}
		if err := add("billing", org.ID, org.Billing, coreRecord{ID: org.ID, OrgID: org.ID, CustomerID: org.Billing.CustomerID, SubscriptionID: org.Billing.SubscriptionID}); err != nil {
			return nil, err
		}
		for _, identity := range []struct{ kind, value string }{{"customer_id", org.Billing.CustomerID}, {"subscription_id", org.Billing.SubscriptionID}} {
			if identity.value != "" {
				if err := unique("billing", org.ID, identity.kind, identity.value); err != nil {
					return nil, err
				}
			}
		}
		for userID, member := range org.Members {
			if member.UserID != userID {
				return nil, core.ErrInvalid
			}
			status := strings.ToLower(member.Status)
			if status == "" {
				status = "active"
			}
			if err := add("memberships", org.ID+":"+userID, member, coreRecord{ID: org.ID + ":" + userID, OrgID: org.ID, UserID: userID, Status: status, CreatedAt: member.CreatedAt}); err != nil {
				return nil, err
			}
		}
		for id, invitation := range org.Invitations {
			if invitation.ID != id || (invitation.OrganizationID != "" && invitation.OrganizationID != org.ID) {
				return nil, core.ErrInvalid
			}
			r := coreRecord{ID: id, OrgID: org.ID, Email: core.NormalizeEmail(invitation.Email), TokenHash: invitation.TokenHash, Status: strings.ToLower(invitation.Status), ExpiresAt: invitation.ExpiresAt, CreatedAt: invitation.CreatedAt}
			if !invitation.ExpiresAt.IsZero() {
				r.PurgeAt = invitation.ExpiresAt.AddDate(0, 0, 30)
			}
			if err := add("invitations", id, invitation, r); err != nil {
				return nil, err
			}
			if invitation.TokenHash != "" {
				if err := unique("invitations", id, "token_hash", invitation.TokenHash); err != nil {
					return nil, err
				}
			}
			if r.Status == "pending" && (invitation.ExpiresAt.IsZero() || now.Before(invitation.ExpiresAt)) {
				if err := unique("invitations", id, "pending", org.ID, r.Email); err != nil {
					return nil, err
				}
			}
		}
		for id, checkout := range org.CheckoutSessions {
			if checkout.ID != id || (checkout.OrganizationID != "" && checkout.OrganizationID != org.ID) {
				return nil, core.ErrInvalid
			}
			r := coreRecord{ID: id, OrgID: org.ID, PlanID: checkout.PlanID, Status: checkout.Status, Demo: checkout.Demo, ExpiresAt: checkout.ExpiresAt, ExpiresAtMS: checkout.ExpiresAt.UnixMilli(), CreatedAt: checkout.CreatedAt}
			if checkout.Demo || checkout.Status != "open" {
				r.PurgeAt = checkout.ExpiresAt.AddDate(0, 0, 30)
			}
			if err := add("checkout_sessions", id, checkout, r); err != nil {
				return nil, err
			}
		}
		for id, project := range org.Projects {
			if project.ID != id || project.OrganizationID != org.ID {
				return nil, core.ErrInvalid
			}
			fields := project
			fields.Databases, fields.APIKeys = nil, nil
			if err := add("projects", id, fields, coreRecord{ID: id, OrgID: org.ID, Name: project.Name, NameKey: strings.ToLower(strings.TrimSpace(project.Name)), DBCount: len(project.Databases), CreatedAt: project.CreatedAt}); err != nil {
				return nil, err
			}
			if err := unique("projects", id, "name", org.ID, strings.ToLower(strings.TrimSpace(project.Name))); err != nil {
				return nil, err
			}
			for id, db := range project.Databases {
				if db.ID != id || db.ProjectID != project.ID {
					return nil, core.ErrInvalid
				}
				if err := encodeDatabase(records, org.ID, project.ID, db); err != nil {
					return nil, err
				}
				if err := unique("databases", id, "name", project.ID, strings.ToLower(strings.TrimSpace(db.Name))); err != nil {
					return nil, err
				}
			}
			for id, key := range project.APIKeys {
				if key.ID != id || key.ProjectID != project.ID {
					return nil, core.ErrInvalid
				}
				if err := add("api_keys", id, key, coreRecord{ID: id, OrgID: org.ID, ProjectID: project.ID, CreatedAt: key.CreatedAt, ExpiresAt: key.ExpiresAt}); err != nil {
					return nil, err
				}
			}
		}
	}
	for _, plan := range state.Plans {
		if err := add("plans", plan.ID, plan, coreRecord{ID: plan.ID, Key: plan.Key, MaxDatabases: plan.MaxDatabases, MaxMembers: plan.MaxMembers}); err != nil {
			return nil, err
		}
	}
	return records, nil
}

// checkCoreSize enforces the published document ceilings before sending a
// transaction, so embedded bounded project/profile payloads cannot overflow.
func checkCoreSize(r coreRecord) error {
	body, err := json.Marshal(r)
	if err != nil {
		return fmt.Errorf("%w: %v", core.ErrInvalid, err)
	}
	if len(body) > 1<<20 {
		return core.Invalid("dashboard record exceeds 1 MiB")
	}
	var data any
	if err := v1.Decode(body, &data); err != nil {
		return fmt.Errorf("%w: %v", core.ErrInvalid, err)
	}
	nodes := 0
	var visit func(any, int) bool
	visit = func(value any, depth int) bool {
		nodes++
		if nodes > 4096 || depth > 16 {
			return false
		}
		switch value := value.(type) {
		case map[string]any:
			for _, child := range value {
				if !visit(child, depth+1) {
					return false
				}
			}
		case []any:
			for _, child := range value {
				if !visit(child, depth+1) {
					return false
				}
			}
		}
		return true
	}
	if !visit(data, 0) {
		return core.Invalid("dashboard record exceeds 4096 nodes or depth 16")
	}
	return nil
}
