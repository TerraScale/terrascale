package v1

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
)

// CollectionAccessPolicy grants access to owners, every authenticated end-user,
// or trusted case-sensitive roles. Public never grants anonymous access.
// Roles is required, even when empty. All-false grants with [] deny access.
type CollectionAccessPolicy struct {
	Owner  bool     `json:"owner"`
	Public bool     `json:"public"`
	Roles  []string `json:"roles"`
}

// CollectionAccessRules is document collection metadata, separate from file
// bucket rules. Owner is an optional JSON Pointer to an immutable signed subject.
// The server validates its pointer and bounded roles; writes cannot transfer
// ownership or recreate another user's deleted document identity.
type CollectionAccessRules struct {
	Owner *string                 `json:"owner,omitempty"`
	Read  *CollectionAccessPolicy `json:"read"`
	Write *CollectionAccessPolicy `json:"write"`
}

// CollectionRules contains the current policy. Nil Rules denies every end-user
// operation; declaring public grants still requires authenticated end-users.
type CollectionRules struct {
	Rules *CollectionAccessRules `json:"rules"`
}

// Rules reads collection rules with a project credential and documents:read.
// End-user credentials are refused with 403; metadata authorization is preserved.
func (col *Collection) Rules(ctx context.Context) (*CollectionRules, error) {
	return col.rulesCall(ctx, http.MethodGet, nil)
}

// SetRules replaces rules with a project credential and documents:write. Both
// read/write policies and their roles arrays are required; use all-false grants
// with empty arrays to deny access. The mutation is never replayed automatically.
func (col *Collection) SetRules(ctx context.Context, in CollectionAccessRules) (*CollectionRules, error) {
	if err := checkCollectionRules(&in); err != nil {
		return nil, err
	}
	body, err := json.Marshal(in)
	if err != nil {
		return nil, err
	}
	if len(body) > 16<<10 {
		return nil, errors.New("terrabase: collection rules exceed 16 KiB")
	}
	return col.rulesCall(ctx, http.MethodPut, body)
}

func checkCollectionRules(r *CollectionAccessRules) error {
	if r != nil && (r.Read == nil || r.Write == nil || r.Read.Roles == nil || r.Write.Roles == nil) {
		return errors.New("terrabase: collection rules require read/write policies with roles arrays")
	}
	return nil
}

func (col *Collection) rulesCall(ctx context.Context, method string, body []byte) (*CollectionRules, error) {
	p, err := col.path("rules")
	if err != nil {
		return nil, err
	}
	var wire struct {
		Rules json.RawMessage `json:"rules"`
	}
	_, err = col.db.c.call(ctx, request{method: method, path: p, body: body, replaySafe: method == http.MethodGet}, nil, &wire)
	if err != nil {
		return nil, err
	}
	var out CollectionRules
	if len(wire.Rules) == 0 || decodeExact(wire.Rules, &out.Rules) != nil {
		return nil, errors.New("terrabase: invalid collection rules response")
	}
	if err := checkCollectionRules(out.Rules); err != nil {
		return nil, err
	}
	return &out, nil
}
