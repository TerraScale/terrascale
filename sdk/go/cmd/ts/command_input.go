package main

import "io"

// commandInput carries the caller's input and consent through authenticated dispatch.
type commandInput struct {
	in     io.Reader
	prompt io.Writer
	tty    bool
	yes    bool
}
