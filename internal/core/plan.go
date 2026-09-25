package core

import "fmt"

func (plan Plan) StorageAllowance() string {
	if plan.StorageLabel != "" {
		return plan.StorageLabel
	}
	if plan.StorageMiB > 0 {
		return fmt.Sprintf("%d MiB storage", plan.StorageMiB)
	}
	return "Limit not published"
}

func (plan Plan) OperationsAllowance() string {
	if plan.OperationsPerMonth > 0 {
		if plan.OperationsPerMonth%1_000_000 == 0 {
			return fmt.Sprintf("%dM operations/month", plan.OperationsPerMonth/1_000_000)
		}
		return fmt.Sprintf("%d operations/month", plan.OperationsPerMonth)
	}
	if plan.MonthlyRequests > 0 {
		return fmt.Sprintf("%d requests/month", plan.MonthlyRequests)
	}
	return "Limit not published"
}

func (plan Plan) DatabaseAllowance() string {
	if plan.MaxDatabases > 0 {
		return fmt.Sprintf("%d databases", plan.MaxDatabases)
	}
	return "Limit not published"
}

func (plan Plan) MemberAllowance() string {
	if plan.MaxMembers > 0 {
		return fmt.Sprintf("%d team members", plan.MaxMembers)
	}
	return "Limit not published"
}
