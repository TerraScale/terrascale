package core

import "fmt"

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

// compactCount writes whole thousands and millions as K and M.
func compactCount(n int64) string {
	switch {
	case n >= 1_000_000 && n%1_000_000 == 0:
		return fmt.Sprintf("%dM", n/1_000_000)
	case n >= 1_000 && n%1_000 == 0:
		return fmt.Sprintf("%dK", n/1_000)
	}
	return fmt.Sprint(n)
}

func countOf(n int, noun string) string {
	if n == 1 {
		return "1 " + noun
	}
	return fmt.Sprintf("%d %ss", n, noun)
}
