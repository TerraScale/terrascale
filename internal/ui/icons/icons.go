// Package icons renders the dashboard's lucide icon set as inline, decorative
// SVG so icons inherit currentColor and never need an extra request.
package icons

import (
	"context"
	"io"
	"strconv"

	"github.com/a-h/templ"
)

func svg(paths string, size int) templ.Component {
	return templ.ComponentFunc(func(_ context.Context, w io.Writer) error {
		s := strconv.Itoa(size)
		_, err := io.WriteString(w, `<svg xmlns="http://www.w3.org/2000/svg" width="`+s+`" height="`+s+
			`" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">`+
			paths+`</svg>`)
		return err
	})
}
