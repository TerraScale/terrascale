// Package demovisuals supplies illustrative charts for the seeded Mario demo.
// These values are never used as provider usage or billing facts.
package demovisuals

import (
	"fmt"
	"math"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

const (
	MarioOrganizationID = "7N1PZ3BD9WCGBVV4V50V508NRX"
	MarioUserID         = "7CVZQJY80VY06TJG7VP8TQ44DM"
)

const (
	SampleStorageMiB      int64 = 6656
	SampleStorageCapacity int64 = 8192
	SampleWrites          int64 = 8_140_200
	SampleWriteCapacity   int64 = 5_000_000
)

func IsSeededMarioAccount(demo bool, account core.Account, org core.Organization) bool {
	return demo && account.User.ID == MarioUserID && org.ID == MarioOrganizationID && org.Slug == "terrascale-labs"
}

func NormalizeRange(value string) string {
	switch value {
	case "24h", "7d", "30d":
		return value
	default:
		return "7d"
	}
}

func RangeLabel(value string) string {
	switch NormalizeRange(value) {
	case "24h":
		return "Last 24 hours"
	case "30d":
		return "Last 30 days"
	default:
		return "Last 7 days"
	}
}

func TickLabels(value string) []string {
	switch NormalizeRange(value) {
	case "24h":
		return []string{"24h ago", "18h", "12h", "6h", "Now"}
	case "30d":
		return []string{"30d ago", "22d", "15d", "7d", "Now"}
	default:
		return []string{"7d ago", "5d", "3d", "1d", "Now"}
	}
}

// PointLabels names count samples that split the range into equal buckets by
// their distance from now, for chart tooltips.
func PointLabels(value string, count int) []string {
	span := 7 * 24 * time.Hour
	switch NormalizeRange(value) {
	case "24h":
		span = 24 * time.Hour
	case "30d":
		span = 30 * 24 * time.Hour
	}
	labels := make([]string, count)
	for i := range labels {
		labels[i] = ago(span * time.Duration(count-1-i) / time.Duration(count))
	}
	return labels
}

func ago(elapsed time.Duration) string {
	days, hours := int(elapsed/(24*time.Hour)), int(elapsed%(24*time.Hour)/time.Hour)
	switch {
	case days == 0 && hours == 0:
		return "Now"
	case days == 0:
		return fmt.Sprintf("%dh ago", hours)
	case hours == 0:
		return fmt.Sprintf("%dd ago", days)
	default:
		return fmt.Sprintf("%dd %dh ago", days, hours)
	}
}

// Workload matches the original demo's seeded Random(42) sequence, range sizes,
// baselines, variance, and step formula. It is illustrative, not telemetry.
func Workload(value string) []int64 {
	count, baseline, variance := 168, 880.0, 320.0
	switch NormalizeRange(value) {
	case "24h":
		count, baseline, variance = 24, 950, 180
	case "30d":
		count, baseline, variance = 30, 820, 420
	}
	result := make([]int64, count)
	random := newSeededRandom(42)
	current := baseline
	for i := range result {
		current = max(50, current+(random.next()-0.48)*variance*0.4)
		result[i] = int64(math.Round(current))
	}
	return result
}

// An explicitly seeded System.Random uses this subtractive sequence in the
// C# source dashboard. Keeping it local makes the sample reproducible in Go.
type seededRandom struct {
	values [56]int64
	nextA  int
	nextB  int
}

func newSeededRandom(seed int64) seededRandom {
	const big = int64(1<<31 - 1)
	random := seededRandom{nextB: 21}
	previous := int64(161803398) - seed
	random.values[55] = previous
	current := int64(1)
	for i := 1; i < 55; i++ {
		index := 21 * i % 55
		random.values[index] = current
		current = previous - current
		if current < 0 {
			current += big
		}
		previous = random.values[index]
	}
	for range 4 {
		for i := 1; i < 56; i++ {
			random.values[i] -= random.values[1+(i+30)%55]
			if random.values[i] < 0 {
				random.values[i] += big
			}
		}
	}
	return random
}

func (random *seededRandom) next() float64 {
	const big = int64(1<<31 - 1)
	random.nextA++
	if random.nextA >= 56 {
		random.nextA = 1
	}
	random.nextB++
	if random.nextB >= 56 {
		random.nextB = 1
	}
	value := random.values[random.nextA] - random.values[random.nextB]
	if value == big {
		value--
	}
	if value < 0 {
		value += big
	}
	random.values[random.nextA] = value
	return float64(value) / float64(big)
}

// Operations splits the illustrative workload into the source dashboard's
// reads, writes, and replication series. All three remain sample data.
func Operations(values []int64) (reads, writes, replication []int64) {
	reads = make([]int64, len(values))
	writes = make([]int64, len(values))
	replication = make([]int64, len(values))
	for i, value := range values {
		reads[i] = int64(math.Round(float64(value) * (0.62 + 0.08*math.Sin(float64(i)*0.35))))
		writes[i] = value - reads[i]
		replication[i] = int64(math.Round(float64(writes[i]) * (0.35 + 0.05*math.Cos(float64(i)*0.5))))
	}
	return reads, writes, replication
}

func Sum(values []int64) int64 {
	var total int64
	for _, value := range values {
		total += value
	}
	return total
}
