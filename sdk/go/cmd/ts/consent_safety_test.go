package main

import (
	"bytes"
	"errors"
	"io"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestResourceDeleteConsent(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"collection", "function"} {
		t.Run(kind, func(t *testing.T) {
			t.Parallel()
			for _, input := range []string{"items\n", "items\r\n", "yes\n", " items \n", "items"} {
				var prompt bytes.Buffer
				err := confirmResourceDelete(kind, "app", "items", "project-id", false, strings.NewReader(input), &prompt, true)
				want := input == "items\n" || input == "items\r\n"
				if (err == nil) != want {
					t.Fatalf("input=%q error=%v", input, err)
				}
				for _, context := range []string{kind, "items", "app", "project-id"} {
					if !strings.Contains(prompt.String(), context) {
						t.Fatalf("missing %q in prompt=%s", context, prompt.String())
					}
				}
			}
			if err := confirmResourceDelete(kind, "app", "items", "", false, forbiddenInput{}, io.Discard, false); err == nil || !strings.Contains(err.Error(), "-yes") {
				t.Fatalf("unattended deletion=%v", err)
			}
			if err := confirmResourceDelete(kind, "app", "items", "", true, forbiddenInput{}, brokenPrompt{}, false); err != nil {
				t.Fatalf("explicit consent=%v", err)
			}
			if err := confirmResourceDelete(kind, "app", "items", "", false, forbiddenInput{}, brokenPrompt{}, true); !errors.Is(err, io.ErrClosedPipe) {
				t.Fatalf("prompt failure=%v", err)
			}
		})
	}
}

func TestImportPreviewConsent(t *testing.T) {
	t.Parallel()
	job := &v1.ImportJob{Database: "app", Project: "project-id", State: "ready", Preview: &v1.ImportPreview{ID: "preview-id", Warnings: 2}}
	var prompt bytes.Buffer
	if err := confirmImport(job, false, strings.NewReader("app\n"), &prompt, true); err != nil {
		t.Fatal(err)
	}
	for _, context := range []string{"app", "preview-id", "project-id", "Warnings: 2"} {
		if !strings.Contains(prompt.String(), context) {
			t.Fatalf("missing %q in prompt=%s", context, prompt.String())
		}
	}
	if err := confirmImport(job, false, strings.NewReader("yes\n"), io.Discard, true); err == nil {
		t.Fatal("wrong database name accepted")
	}
	if err := confirmImport(job, false, forbiddenInput{}, io.Discard, false); err == nil || !strings.Contains(err.Error(), "consent to import") {
		t.Fatalf("unattended import=%v", err)
	}
	if err := confirmImport(job, true, forbiddenInput{}, brokenPrompt{}, false); err != nil {
		t.Fatalf("explicit import=%v", err)
	}
	job.Preview.Blocking = 1
	if err := confirmImport(job, true, forbiddenInput{}, io.Discard, false); err == nil {
		t.Fatal("explicit consent bypassed blocking issues")
	}
	if err := confirmImport(nil, true, forbiddenInput{}, io.Discard, false); err == nil {
		t.Fatal("missing preview accepted")
	}
}
