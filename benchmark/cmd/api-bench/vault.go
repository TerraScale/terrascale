package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"

	"github.com/oklog/ulid/v2"
)

// benchKeyEnv names the operator-placed file holding {"api_key":"<ULID>_<secret>","project":"<ULID>"} for the dedicated benchmark project.
const benchKeyEnv = "TS_BENCH_KEY_FILE"

var (
	apiKeyPattern = regexp.MustCompile(`^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$`)
	errBenchKey   = errors.New(benchKeyEnv + " content is invalid: want {\"api_key\":\"<ULID>_<secret>\",\"project\":\"<ULID>\"}")
)

type benchKey struct {
	APIKey  string `json:"api_key"`
	Project string `json:"project"`
}

// benchCredentials stages the key file named by TS_BENCH_KEY_FILE in a 0600 file inside a fresh 0700 dir; cleanup removes the dir.
func benchCredentials() (string, func(), error) {
	return benchCredentialsFrom(os.Getenv(benchKeyEnv))
}

func benchCredentialsFrom(src string) (string, func(), error) {
	if src == "" {
		return "", nil, fmt.Errorf("%s is not set: point it at the benchmark key JSON file (mode 0600)", benchKeyEnv)
	}
	raw, err := os.ReadFile(src)
	if err != nil {
		return "", nil, fmt.Errorf("%s: cannot read the key file: %w", benchKeyEnv, err)
	}
	defer clear(raw)
	key, err := parseBenchKey(raw)
	if err != nil {
		return "", nil, err
	}
	body, err := json.Marshal(key)
	if err != nil {
		return "", nil, errBenchKey
	}
	defer clear(body)
	return stageBenchCredentials(body)
}

// parseBenchKey validates the key file. Errors never echo its content.
func parseBenchKey(raw []byte) (benchKey, error) {
	var key benchKey
	if err := json.Unmarshal(raw, &key); err != nil || !apiKeyPattern.MatchString(key.APIKey) {
		return benchKey{}, errBenchKey
	}
	if id, err := ulid.Parse(key.Project); err != nil || id.String() != key.Project {
		return benchKey{}, errBenchKey
	}
	return key, nil
}

// stageBenchCredentials writes body to credentials.json in a fresh dir; MkdirTemp creates it 0700.
func stageBenchCredentials(body []byte) (string, func(), error) {
	dir, err := os.MkdirTemp("", "ts-bench-*")
	if err != nil {
		return "", nil, err
	}
	cleanup := func() { _ = os.RemoveAll(dir) }
	path := filepath.Join(dir, "credentials.json")
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		cleanup()
		return "", nil, err
	}
	_, err = f.Write(body)
	if cerr := f.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		cleanup()
		return "", nil, err
	}
	return path, cleanup, nil
}
