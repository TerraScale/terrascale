package ui

import "testing"

func TestFormatBytes(t *testing.T) {
	for n, want := range map[int64]string{0: "0 B", 727: "727 B", 1024: "1.0 KiB", 1536: "1.5 KiB", 1048575: "1.0 MiB", 5 << 20: "5.0 MiB", 3 << 30: "3.0 GiB", 5 << 40: "5.0 TiB"} {
		if got := FormatBytes(n); got != want {
			t.Errorf("FormatBytes(%d) = %q, want %q", n, got, want)
		}
	}
}
