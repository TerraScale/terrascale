package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"time"
)

// FD 3 must be opened before unshare; fail closed when the original namespace is unavailable.
func isolatedNetwork() error {
	original, err := os.Readlink("/proc/self/fd/3")
	if err != nil {
		return fmt.Errorf("WAN requires original network namespace on inherited FD 3: %w", err)
	}
	current, err := os.Readlink("/proc/self/ns/net")
	if err != nil {
		return err
	}
	if !strings.HasPrefix(original, "net:[") || !strings.HasSuffix(original, "]") || original == current {
		return errors.New("WAN refuses shaping outside an isolated network namespace; open FD 3 before unshare")
	}
	return nil
}

// Only proxy-facing TCP is shaped, once per direction; backend, client and admin sockets bypass it.
func shapeWAN(ctx context.Context, ports []int) (cleanup func() error, err error) {
	if err := isolatedNetwork(); err != nil {
		return nil, err
	}
	if len(ports) == 0 || len(ports) > 3 {
		return nil, errors.New("WAN expects one to three peer proxy ports")
	}
	for _, port := range ports {
		if port < 1 || port > 65535 {
			return nil, errors.New("invalid peer proxy port")
		}
	}
	// Add, never replace: an existing namespace qdisc is not ours to overwrite.
	args := []string{"qdisc", "add", "dev", "lo", "root", "handle", "1:", "prio", "bands", strconv.Itoa(len(ports) + 1), "priomap"}
	for range 16 {
		args = append(args, "0")
	}
	if err := tc(ctx, args...); err != nil {
		return nil, err
	}
	remove := func() error {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		return tc(ctx, "qdisc", "del", "dev", "lo", "root", "handle", "1:")
	}
	defer func() {
		if err != nil {
			err = errors.Join(err, remove())
		}
	}()
	for i, port := range ports {
		class := "1:" + strconv.Itoa(i+2)
		if err = tc(ctx, "qdisc", "add", "dev", "lo", "parent", class, "netem", "delay", "100ms", "rate", "100mbit", "limit", "1024"); err != nil {
			return nil, err
		}
		for _, direction := range []string{"dport", "sport"} {
			if err = tc(ctx, "filter", "add", "dev", "lo", "protocol", "ip", "parent", "1:", "prio", "1", "u32",
				"match", "ip", "protocol", "6", "0xff", "match", "ip", direction, strconv.Itoa(port), "0xffff", "flowid", class); err != nil {
				return nil, err
			}
		}
	}
	return remove, nil
}

func tc(ctx context.Context, args ...string) error {
	output, err := exec.CommandContext(ctx, "tc", args...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("tc %s: %w: %s", strings.Join(args, " "), err, strings.TrimSpace(string(output)))
	}
	return nil
}
