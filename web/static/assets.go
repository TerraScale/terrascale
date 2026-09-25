package static

import "embed"

// Assets contains public brand, stylesheet, font, image, and script files.
//
//go:embed brand css fonts img js
var Assets embed.FS
