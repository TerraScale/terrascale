package assets

import (
	"fmt"
	"io/fs"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"time"

	sdk "github.com/starfederation/datastar-go/datastar"

	static "terrascale.tech/dashboard/web/static"
)

func Mount(mux *http.ServeMux, dev bool) error {
	var root fs.FS = static.Assets
	if dev {
		root = os.DirFS("web/static")
	}
	for _, name := range []string{"brand", "css", "fonts", "img", "js"} {
		files, err := fs.Sub(root, name)
		if err != nil {
			return err
		}
		prefix := "/" + name + "/"
		mux.Handle("GET "+prefix, http.StripPrefix(prefix, http.FileServerFS(files)))
	}
	if dev {
		mux.HandleFunc("GET /__dev/events", devEvents)
	}
	return nil
}

// devBoot identifies this process; a page that sees a new boot after the
// stream reconnects reloads, so template and Go changes appear on their own.
var devBoot = strconv.FormatInt(time.Now().UnixNano(), 36)

// devEvents streams Datastar events to pages in development: the boot ID on
// connect, then a stylesheet patch whenever the CSS bundle is rebuilt.
func devEvents(w http.ResponseWriter, r *http.Request) {
	sse := sdk.NewSSE(w, r)
	if err := sse.PatchSignals([]byte(`{"_devBoot":"` + devBoot + `"}`)); err != nil {
		return
	}
	path := filepath.Join("web", "static", "css", "terrascale.css")
	last, _ := os.Stat(path)
	ticker := time.NewTicker(300 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case <-ticker.C:
			current, err := os.Stat(path)
			if err != nil || last == nil {
				last = current
				continue
			}
			if current.ModTime().Equal(last.ModTime()) && current.Size() == last.Size() {
				continue
			}
			last = current
			link := fmt.Sprintf(`<link id="ts-css" rel="stylesheet" href="/css/terrascale.css?v=%d" data-dashboard-css/>`, current.ModTime().UnixNano())
			if err := sse.PatchElements(link); err != nil {
				return
			}
		}
	}
}
