package icons

import (
	"context"
	"strings"
	"testing"
)

func TestIconsRenderSizedDecorativeSVG(t *testing.T) {
	for name, icon := range Named {
		var b strings.Builder
		if err := icon(16).Render(context.Background(), &b); err != nil {
			t.Fatal(name, err)
		}
		out := b.String()
		if !strings.HasPrefix(out, `<svg `) || !strings.Contains(out, `width="16"`) || !strings.Contains(out, `aria-hidden="true"`) || !strings.HasSuffix(out, `</svg>`) || len(out) < 200 {
			t.Fatalf("%s rendered %q", name, out)
		}
	}
}
