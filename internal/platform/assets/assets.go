package assets

import (
	"bytes"
	"fmt"
	"io/fs"
	"mime"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	sdk "github.com/starfederation/datastar-go/datastar"

	static "terrascale.tech/dashboard/web/static"
)

func Mount(mux *http.ServeMux, dev bool) error {
	// Go's built-in table lacks TrueType; the code editor's icon font needs it.
	if err := mime.AddExtensionType(".ttf", "font/ttf"); err != nil {
		return err
	}
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
		mux.Handle("GET "+prefix, cached(dev, http.StripPrefix(prefix, precompressed(files, http.FileServerFS(files)))))
	}
	if dev {
		mux.HandleFunc("GET /__dev/events", devEvents)
	}
	return nil
}

// hashedName matches build outputs whose file name carries their content hash,
// such as js/editor-core.P4OPRQN4.js; they never change under the same name.
var hashedName = regexp.MustCompile(`\.[A-Z0-9]{8}\.[a-z0-9]+$`)

// cached sets the static cache policy. Embedded files have no modification
// time, so the content hash doubles as the ETag; a request for the current
// hash (static.URL) or a content-hashed file name is immutable, anything else
// revalidates within an hour.
func cached(dev bool, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := strings.TrimPrefix(r.URL.Path, "/")
		version := static.Version(name)
		switch {
		case dev || version == "":
			w.Header().Set("Cache-Control", "no-cache")
		case r.URL.Query().Get("v") == version || hashedName.MatchString(name):
			w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
		default:
			w.Header().Set("Cache-Control", "public, max-age=3600")
		}
		if !dev && version != "" {
			w.Header().Set("ETag", `"`+version+`"`)
		}
		next.ServeHTTP(w, r)
	})
}

// precompressed serves the brotli sibling written by release builds
// (scripts/build-js.mjs --release) to clients that accept it; the compression
// middleware leaves responses that already carry a Content-Encoding alone.
func precompressed(files fs.FS, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Add("Vary", "Accept-Encoding")
		if !acceptsBrotli(r.Header.Get("Accept-Encoding")) {
			next.ServeHTTP(w, r)
			return
		}
		name := strings.TrimPrefix(path.Clean("/"+r.URL.Path), "/")
		body, err := fs.ReadFile(files, name+".br")
		if err != nil {
			next.ServeHTTP(w, r)
			return
		}
		// The encoded body is a distinct representation, so it needs its own ETag.
		if etag := w.Header().Get("ETag"); etag != "" {
			w.Header().Set("ETag", strings.TrimSuffix(etag, `"`)+`-br"`)
		}
		w.Header().Set("Content-Type", mime.TypeByExtension(path.Ext(name)))
		w.Header().Set("Content-Encoding", "br")
		http.ServeContent(w, r, name, time.Time{}, bytes.NewReader(body))
	})
}

// acceptsBrotli reports whether an Accept-Encoding header lists br without
// refusing it through q=0.
func acceptsBrotli(header string) bool {
	for coding := range strings.SplitSeq(header, ",") {
		name, params, _ := strings.Cut(coding, ";")
		if strings.TrimSpace(name) == "br" {
			return strings.ReplaceAll(params, " ", "") != "q=0"
		}
	}
	return false
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
