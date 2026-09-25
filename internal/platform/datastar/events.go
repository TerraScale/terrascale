package datastar

import (
	"net/http"

	sdk "github.com/starfederation/datastar-go/datastar"
)

// Events emits official Datastar SSE events. HTML should be produced through
// a templ component before calling PatchElements.
type Events struct{}

func (Events) PatchElements(w http.ResponseWriter, r *http.Request, html string) error {
	return sdk.NewSSE(w, r).PatchElements(html)
}
