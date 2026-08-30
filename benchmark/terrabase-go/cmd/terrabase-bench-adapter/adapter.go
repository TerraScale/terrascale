package main

import (
	"context"
	"errors"
	"fmt"
	"math"
	"os"
	"runtime"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	terrabase "terrascale.tech/terrabase-go"
)

type Sample struct {
	Operations        uint64  `json:"operations"`
	WallSeconds       float64 `json:"wall_seconds"`
	ThroughputOpsPerS float64 `json:"throughput_ops_per_s"`
	P50US             float64 `json:"p50_us"`
	P95US             float64 `json:"p95_us"`
	P99US             float64 `json:"p99_us"`
}

type Environment struct {
	ObservedIdentity string `json:"observed_identity"`
	Endpoint         string `json:"endpoint"`
	ClientTransport  string `json:"client_transport"`
	UnixSeconds      uint64 `json:"unix_seconds"`
	Kernel           string `json:"kernel"`
	LogicalCPUs      int    `json:"logical_cpus"`
}

type RunResults struct {
	Status      string      `json:"status"`
	Target      string      `json:"target"`
	Workload    string      `json:"workload"`
	Config      LaneConfig  `json:"config"`
	Environment Environment `json:"environment"`
	Samples     []Sample    `json:"samples"`
	Limits      []string    `json:"limits"`
}

type FailureResults struct {
	Status      string      `json:"status"`
	Target      string      `json:"target"`
	Workload    string      `json:"workload"`
	Config      LaneConfig  `json:"config"`
	Error       string      `json:"error"`
	Environment Environment `json:"environment"`
	Samples     []Sample    `json:"samples"`
	Limits      []string    `json:"limits"`
}

const (
	collectionName = "users"
	// The standalone benchmark endpoint can honestly attest only that its
	// local durable write completed; regional quorum requires peers that the
	// benchmark process does not own.
	acknowledged = terrabase.AckLocalSynced
	readLevel    = terrabase.ReadLevelRegionalQuorum
)

type slot interface {
	read(context.Context, int) error
	write(context.Context, uint64) error
	query(context.Context, int) error
	close() error
}

type clientSlot struct {
	client    *terrabase.Client
	transport *terrabase.Transport
	slotIndex int
	usage     usageAdmissionFactory
}

// usageAdmissionFactory creates the trusted accounting context for one
// benchmark invocation.  The timestamp is captured once per invocation so
// every mutation has deterministic admission metadata; the request identity
// remains the operation's effective idempotency key on each call.
type usageAdmissionFactory struct {
	project          string
	database         string
	acceptedAtMillis uint64
}

const (
	benchmarkUsagePartition    = "benchmark-partition"
	benchmarkUsageMeterVersion = 1
	benchmarkUsageUnits        = "1"
)

func newUsageAdmissionFactory(config Config) usageAdmissionFactory {
	return usageAdmissionFactory{
		project:          config.Project,
		database:         config.Database,
		acceptedAtMillis: uint64(time.Now().UnixMilli()),
	}
}

func (factory usageAdmissionFactory) admission(requestID string) terrabase.UsageAdmissionContext {
	return terrabase.UsageAdmissionContext{
		Project:          factory.project,
		Database:         factory.database,
		Partition:        benchmarkUsagePartition,
		OriginRegion:     terrabase.UsageRegionGRU,
		RequestID:        requestID,
		AcceptedAtMillis: factory.acceptedAtMillis,
		MeterVersion:     benchmarkUsageMeterVersion,
		IntegerUnits:     benchmarkUsageUnits,
	}
}

func (factory usageAdmissionFactory) option(requestID string) terrabase.CallOption {
	return terrabase.WithUsageAdmission(factory.admission(requestID))
}

func (s *clientSlot) read(ctx context.Context, index int) error {
	_, found, err := s.client.DocumentRead(ctx, collectionName, seededID(index), readLevel)
	if err != nil {
		return err
	}
	if !found {
		return fmt.Errorf("seeded document %q was not found", seededID(index))
	}
	return nil
}

func (s *clientSlot) write(ctx context.Context, index uint64) error {
	writeIndex := uint64(s.slotIndex)*3*maxCount + index
	operationID := fmt.Sprintf("bench-write-%d", writeIndex)
	outcome, err := s.client.DocumentWrite(ctx, terrabase.DocumentPut{
		Collection: collectionName,
		ID:         measuredID(writeIndex),
		Body:       benchmarkBody("measured", 42),
	}, terrabase.ExpectAny(), acknowledged, operationID, s.usage.option(operationID))
	if err != nil {
		return err
	}
	receipt, ok := outcome.Committed()
	if !ok {
		if outcome.Conflict() {
			return errors.New("measured write conflicted")
		}
		if reason, refused := outcome.Refused(); refused {
			return fmt.Errorf("measured write refused: %s", reason)
		}
		return errors.New("measured write returned no committed outcome")
	}
	if receipt.OperationID != operationID {
		return fmt.Errorf("measured write receipt identity %q differs from operation %q", receipt.OperationID, operationID)
	}
	return nil
}

func (s *clientSlot) query(ctx context.Context, index int) error {
	result, err := s.client.QueryExecute(ctx, uint64(s.slotIndex)*maxCount+uint64(index+1),
		fmt.Sprintf("from %s where name = '%s'", collectionName, seededName(index)), 1, readLevel)
	if err != nil {
		return err
	}
	if result.Count == 0 || len(result.Documents) == 0 {
		return fmt.Errorf("seeded query for %q returned no document", seededName(index))
	}
	return nil
}

func (s *clientSlot) close() error {
	clientErr := s.client.Close()
	transportErr := s.transport.Close()
	if clientErr != nil {
		return clientErr
	}
	return transportErr
}

// Run connects, binds, seeds, and measures one complete workload. It never
// starts or stops the harness process.
func Run(ctx context.Context, config Config) (RunResults, error) {
	if err := config.Validate(); err != nil {
		return RunResults{}, err
	}
	if ctx == nil {
		return RunResults{}, errors.New("context is nil")
	}
	ctx, cancel := context.WithTimeout(ctx, config.Timeout)
	defer cancel()
	usage := newUsageAdmissionFactory(config)
	root, err := os.ReadFile(config.RootCertPath)
	if err != nil {
		return RunResults{}, fmt.Errorf("read root certificate: %w", err)
	}
	cert, err := os.ReadFile(config.ClientCertPath)
	if err != nil {
		return RunResults{}, fmt.Errorf("read client certificate: %w", err)
	}
	key, err := os.ReadFile(config.ClientKeyPath)
	if err != nil {
		return RunResults{}, fmt.Errorf("read client key: %w", err)
	}
	options := []terrabase.Option{
		terrabase.WithRootCerts(root),
		terrabase.WithClientCert(cert, key),
	}
	if config.Authority != "" {
		options = append(options, terrabase.WithAuthority(config.Authority))
	}
	transport, err := terrabase.DialTransport(config.Endpoint, options...)
	if err != nil {
		return RunResults{}, fmt.Errorf("dial mTLS endpoint: %w", err)
	}
	scope := terrabase.BindingScope{
		Principal: config.Principal,
		Tenant:    config.Tenant,
		Project:   config.Project,
		Database:  config.Database,
	}

	admin, _, err := transport.Bind(ctx, scope)
	if err != nil {
		_ = transport.Close()
		return RunResults{}, fmt.Errorf("bind seed client: %w", err)
	}
	if err := seed(ctx, admin, config.Lane.SeedDocuments, usage); err != nil {
		admin.Close()
		_ = transport.Close()
		return RunResults{}, err
	}
	admin.Close()
	if err := transport.Close(); err != nil {
		return RunResults{}, fmt.Errorf("close seed transport: %w", err)
	}

	slots := make([]slot, 0, config.Lane.Concurrency)
	for index := 0; index < config.Lane.Concurrency; index++ {
		slotTransport, dialErr := terrabase.DialTransport(config.Endpoint, options...)
		if dialErr != nil {
			for _, opened := range slots {
				opened.close()
			}
			return RunResults{}, fmt.Errorf("dial concurrency slot %d: %w", index, dialErr)
		}
		client, _, bindErr := slotTransport.Bind(ctx, scope)
		if bindErr != nil {
			_ = slotTransport.Close()
			for _, opened := range slots {
				opened.close()
			}
			return RunResults{}, fmt.Errorf("bind concurrency slot %d: %w", index, bindErr)
		}
		slots = append(slots, &clientSlot{client: client, transport: slotTransport, slotIndex: index, usage: usage})
	}
	result, err := runSlots(ctx, config, slots)
	for _, opened := range slots {
		_ = opened.close()
	}
	return result, err
}

func seed(ctx context.Context, client *terrabase.Client, count int, usage usageAdmissionFactory) error {
	for index := 0; index < count; index++ {
		operationID := fmt.Sprintf("bench-seed-%d", index)
		outcome, err := client.DocumentWrite(ctx, terrabase.DocumentPut{
			Collection: collectionName,
			ID:         seededID(index),
			Body:       benchmarkBody(seededName(index), index%100),
		}, terrabase.ExpectAbsent(), acknowledged, operationID, usage.option(operationID))
		if err != nil {
			return fmt.Errorf("seed operation %d: %w", index, err)
		}
		receipt, ok := outcome.Committed()
		if !ok {
			return fmt.Errorf("seed operation %d did not commit", index)
		}
		if receipt.OperationID != operationID {
			return fmt.Errorf("seed operation %d receipt identity %q differs from operation %q", index, receipt.OperationID, operationID)
		}
	}
	return nil
}

func benchmarkBody(name string, group int) terrabase.Value {
	return map[string]terrabase.Value{
		"name":    name,
		"group":   group,
		"payload": payload,
	}
}

const payload = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"

func seededName(index int) string    { return fmt.Sprintf("user-%d", index) }
func seededID(index int) string      { return fmt.Sprintf("seed-%d", index) }
func measuredID(index uint64) string { return fmt.Sprintf("measured-%d", index) }

type barrier struct {
	mu                     sync.Mutex
	cv                     *sync.Cond
	n, arrived, generation int
}

func newBarrier(n int) *barrier {
	b := &barrier{n: n}
	b.cv = sync.NewCond(&b.mu)
	return b
}

func (b *barrier) wait() {
	b.mu.Lock()
	generation := b.generation
	b.arrived++
	if b.arrived == b.n {
		b.arrived = 0
		b.generation++
		b.cv.Broadcast()
		b.mu.Unlock()
		return
	}
	for generation == b.generation {
		b.cv.Wait()
	}
	b.mu.Unlock()
}

func runSlots(ctx context.Context, config Config, slots []slot) (RunResults, error) {
	if err := config.Validate(); err != nil {
		return RunResults{}, err
	}
	if len(slots) != config.Lane.Concurrency {
		return RunResults{}, fmt.Errorf("got %d slots, expected %d", len(slots), config.Lane.Concurrency)
	}
	workload, err := ParseWorkload(config.Workload)
	if err != nil {
		return RunResults{}, err
	}
	participants := len(slots) + 1
	warmup := newBarrier(participants)
	start := newBarrier(participants)
	end := newBarrier(participants)
	var failed atomic.Bool
	var failureMu sync.Mutex
	var failure error
	recordFailure := func(err error) {
		if err == nil {
			return
		}
		failed.Store(true)
		failureMu.Lock()
		if failure == nil {
			failure = err
		}
		failureMu.Unlock()
	}
	perSlotOps := config.Lane.Operations / len(slots)
	operationRemainder := config.Lane.Operations % len(slots)
	perWarmup := config.Lane.WarmupOperations / len(slots)
	warmupRemainder := config.Lane.WarmupOperations % len(slots)
	threadSamples := make([][][]float64, len(slots))
	var workers sync.WaitGroup
	for slotIndex, workerSlot := range slots {
		workers.Add(1)
		go func(slotIndex int, workerSlot slot) {
			defer workers.Done()
			warmupCount := perWarmup
			warmupOffset := slotIndex*perWarmup + min(slotIndex, warmupRemainder)
			if slotIndex < warmupRemainder {
				warmupCount++
			}
			for index := 0; index < warmupCount; index++ {
				if failed.Load() {
					break
				}
				if opErr := doOperation(ctx, workerSlot, workload.SelectMixed(warmupOffset+index), warmupOffset+index+maxCount, config.Lane.SeedDocuments); opErr != nil {
					recordFailure(fmt.Errorf("warm-up operation %d: %w", index, opErr))
					break
				}
			}
			warmup.wait()
			if failed.Load() {
				return
			}
			local := make([][]float64, 0, config.Lane.Samples)
			slotOps := perSlotOps
			if slotIndex < operationRemainder {
				slotOps++
			}
			operationOffset := slotIndex*perSlotOps + min(slotIndex, operationRemainder)
			for sample := 0; sample < config.Lane.Samples; sample++ {
				start.wait()
				latencies := make([]float64, 0, slotOps)
				for index := 0; index < slotOps; index++ {
					if failed.Load() {
						break
					}
					windowIndex := sample*config.Lane.Operations + operationOffset + index
					started := time.Now()
					if opErr := doOperation(ctx, workerSlot, workload.SelectMixed(windowIndex), windowIndex, config.Lane.SeedDocuments); opErr != nil {
						recordFailure(fmt.Errorf("measured operation %d: %w", windowIndex, opErr))
						break
					}
					latencies = append(latencies, float64(time.Since(started).Nanoseconds())/1_000.0)
				}
				end.wait()
				local = append(local, latencies)
			}
			threadSamples[slotIndex] = local
		}(slotIndex, workerSlot)
	}
	warmup.wait()
	if failed.Load() {
		workers.Wait()
		return RunResults{}, failureError(&failureMu, &failure, "warm-up failed")
	}
	walls := make([]float64, 0, config.Lane.Samples)
	for sample := 0; sample < config.Lane.Samples; sample++ {
		start.wait()
		started := time.Now()
		end.wait()
		walls = append(walls, time.Since(started).Seconds())
	}
	workers.Wait()
	if failed.Load() {
		return RunResults{}, failureError(&failureMu, &failure, "measured operation failed")
	}
	samples := make([]Sample, config.Lane.Samples)
	for sample := range samples {
		latencies := make([]float64, 0, config.Lane.Operations)
		for _, worker := range threadSamples {
			latencies = append(latencies, worker[sample]...)
		}
		samples[sample] = SampleOf(uint64(config.Lane.Operations), walls[sample], latencies)
	}
	return RunResults{
		Status:      "complete",
		Target:      targetName,
		Workload:    workload.String(),
		Config:      config.Lane,
		Environment: environmentFor(config),
		Samples:     samples,
		Limits: []string{
			"the harness is an already-started standalone loopback mTLS server; this command does not own its lifecycle",
			"one physical terrabase-go transport and bound client are used per concurrency slot; standalone mutations request local-synced acknowledgement",
			"source identity: " + config.SourceIdentity,
		},
	}, nil
}

func failureError(mu *sync.Mutex, failure *error, fallback string) error {
	mu.Lock()
	defer mu.Unlock()
	if *failure != nil {
		return *failure
	}
	return errors.New(fallback)
}

func doOperation(ctx context.Context, worker slot, workload Workload, index, seeded int) error {
	switch workload {
	case WorkloadRead:
		return worker.read(ctx, index%max(1, seeded))
	case WorkloadWrite:
		return worker.write(ctx, uint64(index))
	case WorkloadQuery:
		return worker.query(ctx, index%max(1, seeded))
	default:
		return errors.New("mixed workload was not resolved")
	}
}

func SampleOf(operations uint64, wallSeconds float64, latencies []float64) Sample {
	values := append([]float64(nil), latencies...)
	for _, value := range values {
		if !finite(value) || value < 0 {
			return Sample{Operations: operations, WallSeconds: wallSeconds}
		}
	}
	sort.Float64s(values)
	percentile := func(fraction float64) float64 {
		if len(values) == 0 {
			return 0
		}
		index := int(math.Round(float64(len(values)-1) * fraction))
		return values[index]
	}
	wall := wallSeconds
	if wall <= 0 {
		wall = math.SmallestNonzeroFloat64
	}
	return Sample{
		Operations:        operations,
		WallSeconds:       wallSeconds,
		ThroughputOpsPerS: float64(operations) / wall,
		P50US:             percentile(.50), P95US: percentile(.95), P99US: percentile(.99),
	}
}

func environmentFor(config Config) Environment {
	kernel := "unknown"
	if data, err := os.ReadFile("/proc/sys/kernel/osrelease"); err == nil {
		kernel = "Linux " + strings.TrimSpace(string(data))
	}
	return Environment{
		ObservedIdentity: config.ServerIdentity,
		Endpoint:         config.Endpoint,
		ClientTransport:  config.TransportIdentity,
		UnixSeconds:      uint64(time.Now().Unix()),
		Kernel:           kernel,
		LogicalCPUs:      runtime.NumCPU(),
	}
}

func finite(value float64) bool { return !math.IsNaN(value) && !math.IsInf(value, 0) }

func max(left, right int) int {
	if left > right {
		return left
	}
	return right
}

func min(left, right int) int {
	if left < right {
		return left
	}
	return right
}

type Workload uint8

const (
	WorkloadRead Workload = iota
	WorkloadWrite
	WorkloadQuery
	WorkloadMixed
)

func ParseWorkload(value string) (Workload, error) {
	switch value {
	case "read":
		return WorkloadRead, nil
	case "write":
		return WorkloadWrite, nil
	case "query":
		return WorkloadQuery, nil
	case "mixed":
		return WorkloadMixed, nil
	default:
		return 0, fmt.Errorf("workload must be read, write, query, or mixed")
	}
}

func (w Workload) String() string {
	switch w {
	case WorkloadRead:
		return "read"
	case WorkloadWrite:
		return "write"
	case WorkloadQuery:
		return "query"
	case WorkloadMixed:
		return "mixed"
	default:
		return "unknown"
	}
}

func (w Workload) SelectMixed(index int) Workload {
	if w != WorkloadMixed {
		return w
	}
	if index%10 < 5 {
		return WorkloadRead
	}
	if index%10 < 8 {
		return WorkloadWrite
	}
	return WorkloadQuery
}
