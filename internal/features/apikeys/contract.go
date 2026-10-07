package apikeys

// The dashboard's project API key contract under /api/v1/projects/…/auth/api-keys.
// ts-go dropped its client for these routes (TS-590); ts-auth owns API keys
// from TS-598 on, and TS-599 replaces these routes.

// ProjectAPIKey is a key without its secret, which only Create and Rotate
// return.
type ProjectAPIKey struct {
	ID         string   `json:"keyId"`
	ProjectID  string   `json:"projectId"`
	Name       string   `json:"name"`
	Prefix     string   `json:"prefix"`
	LastFour   string   `json:"lastFour"`
	Scopes     []string `json:"scopes"`
	DatabaseID string   `json:"databaseId"`
	CreatedBy  string   `json:"createdByEmail"`
	CreatedAt  string   `json:"createdAt"`
	ExpiresAt  string   `json:"expiresAt"`
	LastUsedAt string   `json:"lastUsedAt"`
	RevokedAt  string   `json:"revokedAt"`
	Active     bool     `json:"isActive"`
}

type ProjectAPIKeyPage struct {
	APIKeys    []ProjectAPIKey `json:"apiKeys"`
	NextCursor string          `json:"nextCursor"`
}

type CreateProjectAPIKeyRequest struct {
	Name       string   `json:"name"`
	Scopes     []string `json:"scopes"`
	DatabaseID string   `json:"databaseId,omitempty"`
}

type ProjectAPIKeySecret struct {
	APIKey ProjectAPIKey `json:"apiKey"`
	Secret string        `json:"secret"`
}

type ProjectAPIKeyRevocation struct {
	KeyID   string `json:"keyId"`
	Revoked bool   `json:"revoked"`
}
