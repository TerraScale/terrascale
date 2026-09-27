package core

import (
	"fmt"
	"strconv"
)

// Allowance labels describe a plan's limits the same way on every billing
// surface. AllowanceUnpublished stands in for a limit the plan doesn't set.
const AllowanceUnpublished = "Limit not published"

func (plan Plan) StorageAllowance() string {
	switch {
	case plan.StorageLabel != "":
		return plan.StorageLabel
	case plan.StorageMiB >= 1024 && plan.StorageMiB%1024 == 0:
		return fmt.Sprintf("%d GiB storage", plan.StorageMiB/1024)
	case plan.StorageMiB > 0:
		return fmt.Sprintf("%d MiB storage", plan.StorageMiB)
	}
	return AllowanceUnpublished
}

func (plan Plan) OperationsAllowance() string {
	switch {
	case plan.OperationsPerMonth > 0:
		return compactCount(plan.OperationsPerMonth) + " operations/month"
	case plan.MonthlyRequests > 0:
		return compactCount(plan.MonthlyRequests) + " requests/month"
	}
	return AllowanceUnpublished
}

func (plan Plan) DatabaseAllowance() string {
	if plan.MaxDatabases > 0 {
		return countOf(plan.MaxDatabases, "database")
	}
	return AllowanceUnpublished
}

func (plan Plan) MemberAllowance() string {
	if plan.MaxMembers > 0 {
		return countOf(plan.MaxMembers, "team member")
	}
	return AllowanceUnpublished
}

// compactCount writes n in the largest unit that shows it exactly: B and M
// with up to two decimals, such as 1.5M, and whole thousands as K, such as
// 250K. An allowance is never rounded; any other value is written in full.
func compactCount(n int64) string {
	for _, unit := range []struct {
		size, step int64
		suffix     string
	}{{1_000_000_000, 10_000_000, "B"}, {1_000_000, 10_000, "M"}, {1_000, 1_000, "K"}} {
		if n >= unit.size && n%unit.step == 0 {
			return strconv.FormatFloat(float64(n)/float64(unit.size), 'f', -1, 64) + unit.suffix
		}
	}
	return strconv.FormatInt(n, 10)
}

func countOf(n int, noun string) string {
	if n == 1 {
		return "1 " + noun
	}
	return fmt.Sprintf("%d %ss", n, noun)
}
