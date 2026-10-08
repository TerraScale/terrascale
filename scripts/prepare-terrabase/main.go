// prepare-terrabase downloads and verifies the SDK's pinned test executable
// before parallel test packages need it.
package main

import (
	"log"

	"terrascale.tech/sdk/terrabasetest"
)

func main() {
	path, err := terrabasetest.Binary()
	if err != nil {
		log.Fatal(err)
	}
	log.Printf("TerraBase test executable ready: %s", path)
}
