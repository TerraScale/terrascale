package repllocal

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
	RunDirectory        string `json:"run_directory"`
	Region              string `json:"region"`
	Machine             string `json:"machine"`
	Organization        string `json:"organization"`
	Project             string `json:"project"`
	Database            string `json:"database"`
	User                string `json:"user"`
	AdvertisedPeerURL   string `json:"advertised_peer_url"`
	ReadinessCollection string `json:"readiness_collection,omitempty"`
}

type WorkerTarget struct {
	URL                string `json:"url"`
	Token              string `json:"token"`
	MetricsURL         string `json:"metrics_url"`
	PeerURL            string `json:"peer_url"`
	PID                int    `json:"pid"`
	Database           string `json:"database"`
	DataDirectory      string `json:"data_directory"`
	RemoteConfirmation bool   `json:"remote_confirmation"`
}

type Worker struct {
	Config workerConfig
	Target WorkerTarget
	Proxy  *WireProxy
	cmd    *exec.Cmd
	exited chan error
}

type Cluster struct {
	Work, Root, secret string
	Nodes              map[string]*Worker
	closed             bool
	Preserve           bool
}

func StartCluster(ctx context.Context, Root string) (*Cluster, error) {
	Work, err := os.MkdirTemp("", "repl-local-")
	if err != nil {
		return nil, err
	}
	secret := make([]byte, 32)
	if _, err := rand.Read(secret); err != nil {
		return nil, err
	}
	c := &Cluster{Work: Work, Root: Root, secret: hex.EncodeToString(secret), Nodes: map[string]*Worker{}}
	base := workerConfig{RunDirectory: Work, Organization: ulid.Make().String(), Project: ulid.Make().String(), Database: ulid.Make().String(), User: ulid.Make().String()}
	if err := os.Mkdir(filepath.Join(Work, "members"), 0o700); err != nil {
		return nil, err
	}
	for _, region := range []string{"gru", "sjc", "fra"} {
		p, err := StartWireProxy(ctx)
		if err != nil {
			c.Close()
			return nil, err
		}
		cfg := base
		cfg.Region, cfg.Machine, cfg.AdvertisedPeerURL = region, ulid.Make().String(), p.URL()
		w := &Worker{Config: cfg, Proxy: p}
		c.Nodes[region] = w
		if err := c.Launch(w); err != nil {
			c.Close()
			return nil, err
		}
	}
	if err := c.Ready(ctx); err != nil {
		c.Preserve = true
		c.Close()
		return nil, err
	}
	return c, nil
}

func WriteJSON(path string, value any) error {
	b, err := json.Marshal(value)
	if err != nil {
		return err
	}
	if err := os.WriteFile(path+".partial", b, 0o600); err != nil {
		return err
	}
	return os.Rename(path+".partial", path)
}

func (c *Cluster) Launch(w *Worker) error {
	dir := filepath.Join(c.Work, w.Config.Region)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, "control"), nil, 0o600); err != nil {
		return err
	}
	path := filepath.Join(dir, "config.json")
	if err := WriteJSON(path, w.Config); err != nil {
		return err
	}
	log, err := os.OpenFile(filepath.Join(dir, "host.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return err
	}
	defer log.Close()
	w.cmd = exec.Command("dotnet", filepath.Join(c.Root, "tests/TerraBase.Slow/bin/Release/net11.0/TerraBase.Slow.dll"),
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

func (c *Cluster) Ready(ctx context.Context) error {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	for {
		ready := 0
		for _, w := range c.Nodes {
			var listening struct {
				PeerURL string `json:"peer_url"`
			}
			dir := filepath.Join(c.Work, w.Config.Region)
			if b, err := os.ReadFile(filepath.Join(dir, "listening.json")); err == nil && json.Unmarshal(b, &listening) == nil && listening.PeerURL != "" {
				if err := w.Proxy.Target(listening.PeerURL); err != nil {
					return err
				}
			}
			if b, err := os.ReadFile(filepath.Join(dir, "target.json")); err == nil && json.Unmarshal(b, &w.Target) == nil && w.Target.PID == w.cmd.Process.Pid {
				ready++
			}
			select {
			case err := <-w.exited:
				w.exited <- err
				return fmt.Errorf("%s host exited: %v; private log: %s", w.Config.Region, err, filepath.Join(dir, "host.log"))
			default:
			}
		}
		if ready == len(c.Nodes) {
			return nil
		}
		select {
		case <-ctx.Done():
			return fmt.Errorf("Cluster readiness: %w; private logs in %s", ctx.Err(), c.Work)
		case <-time.After(50 * time.Millisecond):
		}
	}
}

func (c *Cluster) Targets() replbench.Targets {
	t := replbench.Targets{Kind: "local-processes", Regions: map[string]replbench.RegionTarget{}, DataDirs: map[string]string{}}
	for name, w := range c.Nodes {
		t.Project, t.Database = w.Config.Project, w.Target.Database
		t.Regions[name] = replbench.RegionTarget{URL: w.Target.URL, Token: w.Target.Token}
		t.DataDirs[name] = w.Target.DataDirectory
	}
	return t
}

func (c *Cluster) Stop(w *Worker) error {
	w.Proxy.Suspend()
	if w.exited == nil {
		return nil
	}
	_ = os.Remove(filepath.Join(c.Work, w.Config.Region, "control"))
	var err error
	select {
	case err = <-w.exited:
	case <-time.After(30 * time.Second):
		_ = syscall.Kill(-w.cmd.Process.Pid, syscall.SIGKILL)
		<-w.exited
		err = fmt.Errorf("%s host did not stop in 30 seconds", w.Config.Region)
	}
	w.exited = nil
	return err
}

func (c *Cluster) Close() error {
	if c.closed {
		return nil
	}
	c.closed = true
	var errs []error
	for _, w := range c.Nodes {
		if err := w.Proxy.Close(); err != nil {
			errs = append(errs, err)
		}
	}
	for _, w := range c.Nodes {
		if err := c.Stop(w); err != nil {
			errs = append(errs, err)
		}
	}
	if !c.Preserve {
		if err := os.RemoveAll(c.Work); err != nil {
			errs = append(errs, err)
		}
	}
	return errors.Join(errs...)
}

func (c *Cluster) Bytes() int64 {
	var n int64
	for _, w := range c.Nodes {
		n += w.Proxy.Bytes()
	}
	return n
}
