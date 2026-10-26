package tsauth

import "time"

// Tokens are a sign-in's tokens.
type Tokens struct {
	AccessToken string
	// RefreshToken is set with the offline_access scope.
	RefreshToken string
	// IDToken is set with the openid scope.
	IDToken string
	Scope   string
	Expiry  time.Time
}

// Authentication error codes retained for matching typed API errors.
const (
	ErrInvalidPassword ErrorCode = "invalid_password" // outside the policy: 8 to 1024 UTF-8 bytes
	ErrInvalidCode     ErrorCode = "invalid_code"     // a wrong, expired, used or superseded code, or an unknown address
	ErrInvalidClient   ErrorCode = "invalid_client"   // 401: the client's id or secret
	ErrRateLimited     ErrorCode = "rate_limited"     // 429: see APIError.RetryAfter
)
