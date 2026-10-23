package repllocal

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"maps"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strconv"
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
	return startCluster(ctx, Root, []string{"gru", "sjc", "fra"})
}

func StartPair(ctx context.Context, root string) (*Cluster, error) {
	return startCluster(ctx, root, []string{"gru", "sjc"})
}

func startCluster(ctx context.Context, Root string, regions []string) (*Cluster, error) {
	Work, err := os.MkdirTemp("", "repl-local-")
	if err != nil {
		return nil, err
	}
	secret := make([]byte, 32)
	if _, err := rand.Read(secret); err != nil {
		return nil, errors.Join(err, os.RemoveAll(Work))
	}
	c := &Cluster{Work: Work, Root: Root, secret: hex.EncodeToString(secret), Nodes: map[string]*Worker{}}
	base := workerConfig{RunDirectory: Work, Organization: ulid.Make().String(), Project: ulid.Make().String(), Database: ulid.Make().String(), User: ulid.Make().String()}
	if err := os.Mkdir(filepath.Join(Work, "members"), 0o700); err != nil {
		return nil, errors.Join(err, c.Close())
	}
	for _, region := range regions {
		p, err := StartWireProxy(ctx)
		if err != nil {
			return nil, errors.Join(err, c.Close())
		}
		cfg := base
		cfg.Region, cfg.Machine, cfg.AdvertisedPeerURL = region, ulid.Make().String(), p.URL()
		w := &Worker{Config: cfg, Proxy: p}
		c.Nodes[region] = w
		if err := c.Launch(w); err != nil {
			return nil, errors.Join(err, c.Close())
		}
	}
	if err := c.Ready(ctx); err != nil {
		c.Preserve = true
		return nil, errors.Join(err, c.Close())
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
	if err := prepareWorkerControl(dir); err != nil {
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
	cmd, exited := w.cmd, w.exited
	go func() { exited <- cmd.Wait() }()
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
	// Outgoing channels from this worker pass through the other workers' proxies too.
	for _, peer := range c.Nodes {
		peer.Proxy.beginQuiescence()
	}
	defer func() {
		for _, peer := range c.Nodes {
			peer.Proxy.endQuiescence()
		}
	}()
	err := c.quiesce(w)
	if err == nil {
		w.Proxy.target.Store(nil)
	}
	err = errors.Join(err, c.signalStop(w), c.waitStopped(w, time.Now().Add(30*time.Second)))
	w.Proxy.Suspend()
	return err
}

func prepareWorkerControl(dir string) error {
	// A relaunch must not reuse discovery or acknowledgements from the previous PID.
	for _, name := range []string{"quiesce.request", "quiesced", "listening.json", "target.json", "bootstrap.json"} {
		if err := os.Remove(filepath.Join(dir, name)); err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
	}
	return os.WriteFile(filepath.Join(dir, "control"), nil, 0o600)
}

func (c *Cluster) quiesce(workers ...*Worker) error {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	var errs []error
	var requested []*Worker
	for _, w := range workers {
		if w.exited == nil {
			continue
		}
		select {
		case err := <-w.exited:
			w.exited <- err
			errs = append(errs, fmt.Errorf("%s host exited before quiescence: %w", w.Config.Region, errors.Join(errors.New("unexpected worker exit"), err)))
			continue
		default:
		}
		pid := strconv.Itoa(w.cmd.Process.Pid)
		if err := os.WriteFile(filepath.Join(c.Work, w.Config.Region, "quiesce.request"), []byte(pid), 0o600); err != nil {
			errs = append(errs, err)
			continue
		}
		requested = append(requested, w)
	}
	for _, w := range requested {
		if err := c.awaitQuiescence(ctx, w); err != nil {
			errs = append(errs, fmt.Errorf("%s host quiesce: %w; private work: %s", w.Config.Region, err, c.Work))
		}
	}
	return errors.Join(errs...)
}

func (c *Cluster) awaitQuiescence(ctx context.Context, w *Worker) error {
	for {
		select {
		case err := <-w.exited:
			w.exited <- err
			return errors.Join(errors.New("worker exited before quiescence"), err)
		default:
		}
		ack, err := os.ReadFile(filepath.Join(c.Work, w.Config.Region, "quiesced"))
		if err == nil && string(ack) == strconv.Itoa(w.cmd.Process.Pid) {
			return nil
		}
		if err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(50 * time.Millisecond):
		}
	}
}

func (c *Cluster) signalStop(w *Worker) error {
	if w.exited == nil {
		return nil
	}
	if err := os.Remove(filepath.Join(c.Work, w.Config.Region, "control")); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

func (c *Cluster) waitStopped(w *Worker, deadline time.Time) error {
	if w.exited == nil {
		return nil
	}
	var err error
	select {
	case err = <-w.exited:
	case <-time.After(time.Until(deadline)):
		killErr := syscall.Kill(-w.cmd.Process.Pid, syscall.SIGKILL)
		err = errors.Join(fmt.Errorf("%s host did not stop in 30 seconds", w.Config.Region), killErr, <-w.exited)
	}
	w.exited = nil
	return err
}

func (c *Cluster) Close() error {
	if c.closed {
		return nil
	}
	c.closed = true
	workers := slices.Collect(maps.Values(c.Nodes))
	var errs []error
	// Keep transports alive until every worker has stopped background replication and exited.
	for _, w := range workers {
		w.Proxy.beginQuiescence()
	}
	if err := c.quiesce(workers...); err != nil {
		errs = append(errs, err)
	}
	for _, w := range workers {
		if err := c.signalStop(w); err != nil {
			errs = append(errs, err)
		}
	}
	deadline := time.Now().Add(30 * time.Second)
	for _, w := range workers {
		if err := c.waitStopped(w, deadline); err != nil {
			errs = append(errs, err)
		}
	}
	for _, w := range workers {
		w.Proxy.target.Store(nil)
	}
	for _, w := range workers {
		if err := w.Proxy.Close(); err != nil {
			errs = append(errs, fmt.Errorf("%s proxy close: %w; private work: %s", w.Config.Region, err, c.Work))
		}
	}
	if !c.Preserve && len(errs) == 0 {
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
