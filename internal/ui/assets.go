package ui

import "embed"

// Assets contains the shared HTML layouts used by every feature template set.
// Static web files are embedded by web/static and served separately.
//
//go:embed layouts/*.html
var Assets embed.FS
