package assets

import (
	"fmt"
	"io/fs"
	"net/http"
	"os"
	"path/filepath"
	"time"

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

func devEvents(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("X-Accel-Buffering", "no")
	controller := http.NewResponseController(w)
	if _, err := fmt.Fprint(w, ": connected\n\n"); err != nil {
		return
	}
	if err := controller.Flush(); err != nil {
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
			if !current.ModTime().Equal(last.ModTime()) || current.Size() != last.Size() {
				last = current
				if _, err := fmt.Fprint(w, "event: css\ndata: reload\n\n"); err != nil {
					return
				}
				if err := controller.Flush(); err != nil {
					return
				}
			}
		}
	}
}
