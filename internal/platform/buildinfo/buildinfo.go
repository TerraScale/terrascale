// Package buildinfo reports which build of the dashboard is running.
package buildinfo

import (
	"runtime/debug"
	"sync"
)

// version is stamped at link time by release builds (Dockerfile, Makefile):
//
//	-ldflags '-X terrascale.tech/dashboard/internal/platform/buildinfo.version=<commit>'
var version string

// Version returns the stamped build version, else the VCS revision the Go
// toolchain embedded, else "dev". Health probes publish it so the status
// page can mark every deploy.
var Version = sync.OnceValue(func() string {
	info, _ := debug.ReadBuildInfo()
	return resolve(version, info)
})

func resolve(stamped string, info *debug.BuildInfo) string {
	if stamped != "" {
		return stamped
	}
	if info == nil {
		return "dev"
	}
	revision, dirty := "", false
	for _, setting := range info.Settings {
		switch setting.Key {
		case "vcs.revision":
			revision = setting.Value
		case "vcs.modified":
			dirty = setting.Value == "true"
		}
	}
	switch {
	case revision == "":
		return "dev"
	case dirty:
		return revision + "-dirty"
	default:
		return revision
	}
}
