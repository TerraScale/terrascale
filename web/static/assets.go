package static

import "embed"

// Assets contains public brand, stylesheet, font, and script files.
//
//go:embed brand css fonts js
var Assets embed.FS
