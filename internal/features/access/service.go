package access

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

var ErrInvalidInvitationAction = errors.New("invalid invitation action")

type Service struct {
	store *core.Store
}

func NewService(store *core.Store) Service {
	return Service{store: store}
}

func (s Service) RespondToInvitation(p core.Principal, token, action string) (core.Organization, error) {
	if token == "" {
		return core.Organization{}, core.ErrNotFound
	}
	hash := hashInvitationToken(token)
	switch strings.ToLower(strings.TrimSpace(action)) {
	case "accept":
		return s.store.AcceptInvitation(p, hash)
	case "decline":
		return s.store.DeclineInvitation(p, hash)
	default:
		return core.Organization{}, ErrInvalidInvitationAction
	}
}

func hashInvitationToken(token string) string {
	digest := sha256.Sum256([]byte(token))
	return hex.EncodeToString(digest[:])
}
