package databases

import (
	"slices"
	"strconv"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

type detailInfo struct {
	Tab               string
	Regions           []detailRegion
	BackupPolicy      core.BackupPolicy
	HasBackupSchedule bool
	BackupsEnabled    bool
	CanToggleBackups  bool
	// Backups are listed newest first.
	Backups             []core.Backup
	CanCreateBackups    bool
	CanEditBackupPolicy bool
	CanRestoreBackups   bool
	CanBranch           bool
	Demo                bool
}

type detailRegion struct {
	Name     string
	Estimate string
	Sample   bool
}

func makeDetailInfo(demo bool, principal core.Principal, account core.Account, project core.Project, db core.Database, tab string, canManage, canMutate bool) detailInfo {
	policy := project.BackupPolicies[db.ID]
	hasSchedule := policy.FullBackupCron != "" || policy.IncrementalBackupCron != ""
	info := detailInfo{
		Tab:                 detailTab(tab),
		BackupPolicy:        policy,
		HasBackupSchedule:   hasSchedule,
		BackupsEnabled:      hasSchedule && !policy.Disabled,
		CanToggleBackups:    demo && canMutate && hasSchedule,
		Backups:             slices.SortedFunc(slices.Values(db.Backups), func(a, b core.Backup) int { return b.CreatedAt.Compare(a.CreatedAt) }),
		CanCreateBackups:    demo && canManage,
		CanEditBackupPolicy: demo && canManage,
		CanRestoreBackups:   canMutate,
		CanBranch:           canMutate && db.Status == "ready",
		Demo:                demo,
	}

	// The source dashboard's nearest-region 0 ms is illustrative fixture data,
	// not a measured provider latency. Keep it scoped to that exact demo row.
	sample := demo &&
		principal.UserID == "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB" &&
		account.User.ID == principal.UserID && account.User.Email == "mario@terrascale.tech" &&
		principal.OrganizationID == "org_01JC9Q5V2N4P6T8R0X2Z4B6D8" &&
		principal.ProjectID == project.ID &&
		project.ID == "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA" &&
		db.ID == "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC" &&
		db.ProjectID == project.ID &&
		db.Name == "core-primary" && db.Region == "SFO"
	region := detailRegion{Name: regionDisplay(db.Region), Estimate: "Not measured", Sample: sample}
	if sample {
		region.Estimate = "0 ms · demo estimate"
	}
	info.Regions = append(info.Regions, region)
	if backupRegion := project.BackupRegion; backupRegion != "" && !strings.EqualFold(backupRegion, db.Region) {
		info.Regions = append(info.Regions, detailRegion{Name: regionDisplay(backupRegion) + " · backup region", Estimate: "Not measured"})
	}
	return info
}

func detailTab(tab string) string {
	switch tab {
	case "placement", "backups", "branches", "import":
		return tab
	default:
		return "overview"
	}
}

func regionDisplay(code string) string {
	switch strings.ToUpper(code) {
	case "SFO":
		return "SFO · San Francisco"
	case "GRU":
		return "GRU · São Paulo"
	case "FRA":
		return "FRA · Frankfurt"
	case "":
		return "Not configured"
	default:
		return strings.ToUpper(code)
	}
}

func backupRetentionDays(days int) string {
	if days < 1 {
		return "7"
	}
	return strconv.Itoa(days)
}

func backupToggleDisabled(enabled bool) string {
	if enabled {
		return "1"
	}
	return "0"
}

func backupChecked(enabled bool) string {
	if enabled {
		return "true"
	}
	return "false"
}

func backupStatusDescription(info detailInfo) string {
	if !info.HasBackupSchedule {
		return "No scheduled backup policy is configured."
	}
	if info.BackupsEnabled {
		return "The saved backup policy is enabled."
	}
	return "The saved backup policy is disabled."
}

func backupAvailability(info detailInfo) string {
	switch {
	case !info.Demo:
		return "Scheduling is unavailable in the published SDK contract."
	case !info.HasBackupSchedule:
		return "Configure a schedule to enable backups."
	default:
		return "Only project managers can change this policy."
	}
}

func backupTone(status string) string {
	switch status {
	case "completed", "ready", "succeeded":
		return "success"
	case "failed":
		return "danger"
	default:
		return "neutral"
	}
}

func branchCapability(demo bool) string {
	if demo {
		return "In-memory snapshot copy"
	}
	return "SDK branch request"
}
