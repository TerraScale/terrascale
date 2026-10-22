package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

type workerConfig struct {
	RunDirectory      string `json:"run_directory"`
	Region            string `json:"region"`
	Machine           string `json:"machine"`
	Organization      string `json:"organization"`
	Project           string `json:"project"`
	Database          string `json:"database"`
	User              string `json:"user"`
	AdvertisedPeerURL string `json:"advertised_peer_url"`
}

type workerTarget struct {
	URL                string `json:"url"`
	Token              string `json:"token"`
	MetricsURL         string `json:"metrics_url"`
	PeerURL            string `json:"peer_url"`
	PID                int    `json:"pid"`
	Database           string `json:"database"`
	DataDirectory      string `json:"data_directory"`
	RemoteConfirmation bool   `json:"remote_confirmation"`
}

type worker struct {
	config workerConfig
	target workerTarget
	proxy  *wireProxy
	cmd    *exec.Cmd
	exited chan error
}

type cluster struct {
	work, root, secret string
	nodes              map[string]*worker
	closed             bool
	preserve           bool
}

func startCluster(ctx context.Context, root string) (*cluster, error) {
	work, err := os.MkdirTemp("", "repl-local-")
	if err != nil {
		return nil, err
	}
	secret := make([]byte, 32)
	if _, err := rand.Read(secret); err != nil {
		return nil, err
	}
	c := &cluster{work: work, root: root, secret: hex.EncodeToString(secret), nodes: map[string]*worker{}}
	base := workerConfig{RunDirectory: work, Organization: ulid.Make().String(), Project: ulid.Make().String(), Database: ulid.Make().String(), User: ulid.Make().String()}
	if err := os.Mkdir(filepath.Join(work, "members"), 0o700); err != nil {
		return nil, err
	}
	for _, region := range []string{"gru", "sjc", "fra"} {
		p, err := startWireProxy(ctx)
		if err != nil {
			c.Close()
			return nil, err
		}
		cfg := base
		cfg.Region, cfg.Machine, cfg.AdvertisedPeerURL = region, ulid.Make().String(), p.URL()
		w := &worker{config: cfg, proxy: p}
		c.nodes[region] = w
		if err := c.launch(w); err != nil {
			c.Close()
			return nil, err
		}
	}
	if err := c.ready(ctx); err != nil {
		c.preserve = true
		c.Close()
		return nil, err
	}
	return c, nil
}

func writeJSON(path string, value any) error {
	b, err := json.Marshal(value)
	if err != nil {
		return err
	}
	if err := os.WriteFile(path+".partial", b, 0o600); err != nil {
		return err
	}
	return os.Rename(path+".partial", path)
}

func (c *cluster) launch(w *worker) error {
	dir := filepath.Join(c.work, w.config.Region)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, "control"), nil, 0o600); err != nil {
		return err
	}
	path := filepath.Join(dir, "config.json")
	if err := writeJSON(path, w.config); err != nil {
		return err
	}
	log, err := os.OpenFile(filepath.Join(dir, "host.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return err
	}
	defer log.Close()
	w.cmd = exec.Command("dotnet", filepath.Join(c.root, "tests/TerraBase.Slow/bin/Release/net11.0/TerraBase.Slow.dll"),
		"--treenode-filter", "/*/*/BenchmarkProcessHost/*", "--results-directory", filepath.Join(dir, "results"))
	w.cmd.Env = append(os.Environ(), "TERRABASE_BENCH_PROCESS_CONFIG="+path, "TERRABASE_TEST_PEER_SECRET="+c.secret)
	w.cmd.Stdout, w.cmd.Stderr = log, log
	w.cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	if err := w.cmd.Start(); err != nil {
		return err
	}
	w.exited = make(chan error, 1)
	go func() { w.exited <- w.cmd.Wait() }()
	return nil
}

func (c *cluster) ready(ctx context.Context) error {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	for {
		ready := 0
		for _, w := range c.nodes {
			var listening struct {
				PeerURL string `json:"peer_url"`
			}
			dir := filepath.Join(c.work, w.config.Region)
			if b, err := os.ReadFile(filepath.Join(dir, "listening.json")); err == nil && json.Unmarshal(b, &listening) == nil && listening.PeerURL != "" {
				if err := w.proxy.Target(listening.PeerURL); err != nil {
					return err
				}
			}
			if b, err := os.ReadFile(filepath.Join(dir, "target.json")); err == nil && json.Unmarshal(b, &w.target) == nil && w.target.PID > 0 {
				ready++
			}
			select {
			case err := <-w.exited:
				w.exited <- err
				return fmt.Errorf("%s host exited: %v; private log: %s", w.config.Region, err, filepath.Join(dir, "host.log"))
			default:
			}
		}
		if ready == len(c.nodes) {
			return nil
		}
		select {
		case <-ctx.Done():
			return fmt.Errorf("cluster readiness: %w; private logs in %s", ctx.Err(), c.work)
		case <-time.After(50 * time.Millisecond):
		}
	}
}

func (c *cluster) targets() replbench.Targets {
	t := replbench.Targets{Kind: "local-processes", Regions: map[string]replbench.RegionTarget{}, DataDirs: map[string]string{}}
	for name, w := range c.nodes {
		t.Project, t.Database = w.config.Project, w.target.Database
		t.Regions[name] = replbench.RegionTarget{URL: w.target.URL, Token: w.target.Token}
		t.DataDirs[name] = w.target.DataDirectory
	}
	return t
}

func (c *cluster) stop(w *worker) error {
	if w.exited == nil {
		return nil
	}
	_ = os.Remove(filepath.Join(c.work, w.config.Region, "control"))
	var err error
	select {
	case err = <-w.exited:
	case <-time.After(30 * time.Second):
		_ = syscall.Kill(-w.cmd.Process.Pid, syscall.SIGKILL)
		<-w.exited
		err = fmt.Errorf("%s host did not stop in 30 seconds", w.config.Region)
	}
	w.exited = nil
	return err
}

func (c *cluster) Close() error {
	if c.closed {
		return nil
	}
	c.closed = true
	var errs []error
	for _, w := range c.nodes {
		if err := c.stop(w); err != nil {
			errs = append(errs, err)
		}
		if err := w.proxy.Close(); err != nil {
			errs = append(errs, err)
		}
	}
	if !c.preserve {
		if err := os.RemoveAll(c.work); err != nil {
			errs = append(errs, err)
		}
	}
	return errors.Join(errs...)
}
