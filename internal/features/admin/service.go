package admin

import (
	"errors"
	"fmt"
	"slices"
	"strings"
	"sync"
	"unicode"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

var estateActionKinds = [...]string{
	"pause", "resume", "guard", "spendCap", "trial", "refund", "reaperExemption", "flag", "viewAs",
}

type estateRegionDefinition struct {
	Code     string
	Location string
}

var estateRegionDefinitions = [...]estateRegionDefinition{
	{Code: string(sdk.RegionGRU), Location: "São Paulo"},
	{Code: string(sdk.RegionSFO), Location: "San Francisco"},
	{Code: string(sdk.RegionFRA), Location: "Frankfurt"},
}

// Service applies platform administrator actions through the store's guarded
// cross-account operations. Authorization always comes from persisted state.
type Service struct {
	store      *core.Store
	mutationMu *sync.Mutex
}

func NewService(store *core.Store) Service {
	return Service{store: store, mutationMu: new(sync.Mutex)}
}

func (s Service) page(principal core.Principal, section, rawQuery string) (pageData, error) {
	accounts, err := s.platformAccounts(principal)
	if err != nil {
		return pageData{}, err
	}
	query := truncateQuery(rawQuery)
	data := pageData{Section: section, Query: query, Inventory: summarizeInventory(accounts)}
	accountIDsByUser := make(map[string]string, len(accounts))
	for _, account := range accounts {
		accountIDsByUser[account.User.ID] = account.ID
	}
	switch section {
	case "users":
		data.Title = "Users"
		for _, account := range accounts {
			projectCount := 0
			for _, org := range account.Organizations {
				projectCount += len(org.Projects)
			}
			row := userRow{
				AccountID: account.ID, UserID: account.User.ID, Name: account.User.Name,
				Email: account.User.Email, PlatformAdmin: account.User.PlatformAdmin,
				CreatedAt:         formatTime(account.User.CreatedAt),
				OrganizationCount: len(account.Organizations), ProjectCount: projectCount,
			}
			if matching(query, row.Name, row.Email, row.UserID) {
				data.Users = append(data.Users, row)
			}
		}
	case "organizations":
		data.Title = "Organizations"
		ownerEmails := make(map[string]string, len(accounts))
		for _, account := range accounts {
			ownerEmails[account.User.ID] = account.User.Email
		}
		seen := make(map[string]struct{})
		for _, account := range accounts {
			for _, org := range account.Organizations {
				if _, exists := seen[org.ID]; exists {
					continue
				}
				seen[org.ID] = struct{}{}
				ownerAccountID := accountIDsByUser[org.OwnerUserID]
				if ownerAccountID == "" {
					ownerAccountID = account.ID
				}
				row := organizationRow{
					AccountID: ownerAccountID, ID: org.ID, Name: org.Name, Status: org.Status,
					OwnerEmail: ownerEmails[org.OwnerUserID], ProjectCount: len(org.Projects), CreatedAt: formatTime(org.CreatedAt),
				}
				if matching(query, row.ID, row.Name, row.Status, row.OwnerEmail) {
					data.Organizations = append(data.Organizations, row)
				}
			}
		}
	case "projects":
		data.Title = "Projects"
		seen := make(map[string]struct{})
		for _, account := range accounts {
			for _, org := range account.Organizations {
				for _, project := range org.Projects {
					if _, exists := seen[project.ID]; exists {
						continue
					}
					seen[project.ID] = struct{}{}
					ownerAccountID := accountIDsByUser[org.OwnerUserID]
					if ownerAccountID == "" {
						ownerAccountID = account.ID
					}
					row := projectRow{
						AccountID: ownerAccountID, OrganizationID: org.ID, Organization: org.Name,
						ID: project.ID, Name: project.Name, Region: project.Region, Status: project.Status,
						DatabaseCount: len(project.Databases), CreatedAt: formatTime(project.CreatedAt),
					}
					if matching(query, row.ID, row.Name, row.Organization, row.Region, row.Status) {
						data.Projects = append(data.Projects, row)
					}
				}
			}
		}
	case "databases":
		data.Title = "Databases"
		seen := make(map[string]struct{})
		for _, account := range accounts {
			for _, org := range account.Organizations {
				for _, project := range org.Projects {
					for _, database := range project.Databases {
						if _, exists := seen[database.ID]; exists {
							continue
						}
						seen[database.ID] = struct{}{}
						ownerAccountID := accountIDsByUser[org.OwnerUserID]
						if ownerAccountID == "" {
							ownerAccountID = account.ID
						}
						row := databaseRow{
							AccountID: ownerAccountID, OrganizationID: org.ID, Organization: org.Name,
							ProjectID: project.ID, Project: project.Name,
							ID: database.ID, Name: database.Name,
							Region: adminFallback(database.Region, "Not recorded"),
							Status: adminFallback(database.Status, "Unknown"), CreatedAt: formatTime(database.CreatedAt),
						}
						if matching(query, row.ID, row.Name, row.Region, row.Status, row.Organization, row.Project) {
							data.Databases = append(data.Databases, row)
						}
					}
				}
			}
		}
	case "estate":
		data.Title = "Estate"
		data.EstateRegionCount = len(estateRegionDefinitions)
		regions := make(map[string]*estateRow)
		seenProjects := make(map[string]struct{})
		seenDatabases := make(map[string]struct{})
		for _, account := range accounts {
			for _, org := range account.Organizations {
				for _, project := range org.Projects {
					if _, exists := seenProjects[project.ID]; exists {
						continue
					}
					seenProjects[project.ID] = struct{}{}
					projectRegions := make(map[string]struct{})
					addProjectRegion := func(raw string) {
						region := normalizedObservedRegion(raw)
						if region != "" {
							projectRegions[region] = struct{}{}
						}
					}
					addProjectRegion(project.Region)
					addProjectRegion(project.BackupRegion)
					for _, region := range project.Regions {
						addProjectRegion(region)
					}
					for region := range projectRegions {
						observed := observedRegion(regions, region)
						observed.ObservedProjects++
					}
					for _, database := range project.Databases {
						if _, exists := seenDatabases[database.ID]; exists {
							continue
						}
						seenDatabases[database.ID] = struct{}{}
						data.EstateDatabaseCount++
						ownerAccountID := accountIDsByUser[org.OwnerUserID]
						if ownerAccountID == "" {
							ownerAccountID = account.ID
						}
						row := databaseRow{
							AccountID: ownerAccountID, OrganizationID: org.ID, Organization: org.Name,
							ProjectID: project.ID, Project: project.Name,
							ID: database.ID, Name: database.Name,
							Region: adminFallback(database.Region, "Not recorded"),
							Status: adminFallback(database.Status, "Unknown"), CreatedAt: formatTime(database.CreatedAt),
						}
						if matching(query, row.ID, row.Name, row.Region, row.Status, row.Organization, row.Project) {
							data.EstateDatabases = append(data.EstateDatabases, row)
						}
						region := normalizedObservedRegion(database.Region)
						if region != "" {
							observedRegion(regions, region).ObservedDatabases++
						}
					}
				}
			}
		}
		for _, definition := range estateRegionDefinitions {
			region := observedRegion(regions, definition.Code)
			region.Location = definition.Location
			if matching(query, region.Region, region.Location) {
				data.Regions = append(data.Regions, *region)
			}
			delete(regions, definition.Code)
		}
		for _, region := range regions {
			if matching(query, region.Region) {
				data.Regions = append(data.Regions, *region)
			}
		}
		data.EstateActions, err = s.store.PlatformAdminActions(principal)
		if err != nil {
			return pageData{}, err
		}
		data.EstateActionCount = len(data.EstateActions)
		slices.Reverse(data.EstateActions)
	case "usage":
		data.Title = "Usage"
	case "plans":
		data.Title = "Plans"
		for _, plan := range s.store.Plans() {
			if matching(query, plan.ID, plan.Key, plan.Name) {
				data.Plans = append(data.Plans, plan)
			}
		}
	default:
		return pageData{}, core.ErrNotFound
	}
	sortRows(&data)
	return data, nil
}

func summarizeInventory(accounts []core.Account) inventoryCounts {
	counts := inventoryCounts{Accounts: len(accounts)}
	seenOrganizations := make(map[string]struct{})
	seenProjects := make(map[string]struct{})
	seenDatabases := make(map[string]struct{})
	for _, account := range accounts {
		for _, organization := range account.Organizations {
			if _, exists := seenOrganizations[organization.ID]; exists {
				continue
			}
			seenOrganizations[organization.ID] = struct{}{}
			counts.Organizations++
			for _, project := range organization.Projects {
				if _, exists := seenProjects[project.ID]; exists {
					continue
				}
				seenProjects[project.ID] = struct{}{}
				counts.Projects++
				for _, database := range project.Databases {
					if _, exists := seenDatabases[database.ID]; exists {
						continue
					}
					seenDatabases[database.ID] = struct{}{}
					counts.Databases++
				}
			}
		}
	}
	return counts
}

func normalizedObservedRegion(region string) string {
	return strings.ToUpper(strings.TrimSpace(region))
}

func observedRegion(regions map[string]*estateRow, code string) *estateRow {
	region := regions[code]
	if region == nil {
		region = &estateRow{Region: code}
		regions[code] = region
	}
	return region
}

func (s Service) UpdateUser(principal core.Principal, accountID, name string, platformAdmin bool) error {
	unlock := s.lockMutations()
	defer unlock()
	accounts, err := s.platformAccounts(principal)
	if err != nil {
		return err
	}
	name, err = validName(name)
	if err != nil {
		return err
	}
	var target *core.Account
	adminCount := 0
	for i := range accounts {
		if accounts[i].User.PlatformAdmin {
			adminCount++
		}
		if accounts[i].ID == accountID {
			target = &accounts[i]
		}
	}
	if target == nil {
		return fmt.Errorf("%w: account %q", core.ErrNotFound, accountID)
	}
	if target.User.PlatformAdmin && !platformAdmin && adminCount <= 1 {
		return fmt.Errorf("%w: the last platform administrator cannot be demoted", core.ErrConflict)
	}
	return s.store.UpdateAccountAsAdmin(principal, accountID, func(account *core.Account) error {
		account.User.Name = name
		account.User.PlatformAdmin = platformAdmin
		return nil
	})
}

// RecordEstateAction validates and records a product action without changing
// estate, billing, tenant, or infrastructure state.
func (s Service) RecordEstateAction(principal core.Principal, action, target, reason string) error {
	if err := s.requireAdmin(principal); err != nil {
		return err
	}
	if !slices.Contains(estateActionKinds[:], action) {
		return core.Invalid("action must be one of " + strings.Join(estateActionKinds[:], ", "))
	}
	target = strings.TrimSpace(target)
	if target == "" {
		return core.Invalid("enter a target")
	}
	reason = strings.TrimSpace(reason)
	reasonLength := utf8.RuneCountInString(reason)
	if reasonLength < 3 || reasonLength > 1000 {
		return core.Invalid("provide a reason between 3 and 1000 characters")
	}
	return s.store.RecordPlatformAdminAction(principal, action, target, reason)
}

func (s Service) RenameOrganization(principal core.Principal, accountID, organizationID, name string) error {
	if err := s.requireAdmin(principal); err != nil {
		return err
	}
	name, err := validName(name)
	if err != nil {
		return err
	}
	return s.store.UpdateAccountAsAdmin(principal, accountID, func(account *core.Account) error {
		org, ok := account.Organizations[organizationID]
		if !ok {
			return fmt.Errorf("%w: organization %q", core.ErrNotFound, organizationID)
		}
		org.Name = name
		account.Organizations[organizationID] = org
		return nil
	})
}

func (s Service) RenameProject(principal core.Principal, accountID, organizationID, projectID, name string) error {
	if err := s.requireAdmin(principal); err != nil {
		return err
	}
	name, err := validName(name)
	if err != nil {
		return err
	}
	return s.store.UpdateAccountAsAdmin(principal, accountID, func(account *core.Account) error {
		org, ok := account.Organizations[organizationID]
		if !ok {
			return fmt.Errorf("%w: organization %q", core.ErrNotFound, organizationID)
		}
		project, ok := org.Projects[projectID]
		if !ok {
			return fmt.Errorf("%w: project %q", core.ErrNotFound, projectID)
		}
		project.Name = name
		org.Projects[projectID] = project
		account.Organizations[organizationID] = org
		return nil
	})
}

// SavePlan creates a plan when plan.ID is empty and otherwise updates the
// catalog fields the administrator edits. Limits the editor does not manage,
// such as database and member caps, keep their saved values.
func (s Service) SavePlan(principal core.Principal, plan core.Plan) (core.Plan, error) {
	unlock := s.lockMutations()
	defer unlock()
	if err := s.requireAdmin(principal); err != nil {
		return core.Plan{}, err
	}
	var err error
	if plan.Name, err = validName(plan.Name); err != nil {
		return core.Plan{}, err
	}
	if !validPlanKey(plan.Key) {
		return core.Plan{}, core.Invalid("plan keys use up to 64 lowercase letters, digits, hyphens, and underscores")
	}
	if plan.PriceCents < 0 || plan.AnnualPriceCents < 0 || plan.TrialDays < 0 || plan.StorageMiB < 0 || plan.MonthlyRequests < 0 {
		return core.Plan{}, core.Invalid("plan prices and limits must be non-negative")
	}
	plans := s.store.Plans()
	if plan.ID == "" {
		plan.ID = core.NewID("plan")
	} else {
		index := slices.IndexFunc(plans, func(existing core.Plan) bool { return existing.ID == plan.ID })
		if index < 0 {
			return core.Plan{}, fmt.Errorf("%w: plan %q", core.ErrNotFound, plan.ID)
		}
		existing := plans[index]
		plan.MaxDatabases, plan.MaxMembers = existing.MaxDatabases, existing.MaxMembers
		plan.OperationsPerMonth, plan.StorageLabel = existing.OperationsPerMonth, existing.StorageLabel
	}
	for _, existing := range plans {
		if existing.ID != plan.ID && strings.EqualFold(existing.Key, plan.Key) {
			return core.Plan{}, fmt.Errorf("%w: plan key %q is already used", core.ErrConflict, plan.Key)
		}
	}
	if err := s.store.UpsertPlanAs(principal, plan); err != nil {
		return core.Plan{}, err
	}
	return plan, nil
}

func (s Service) SetPlanVisibility(principal core.Principal, planID string, visible bool) error {
	unlock := s.lockMutations()
	defer unlock()
	if err := s.requireAdmin(principal); err != nil {
		return err
	}
	plans := s.store.Plans()
	for _, plan := range plans {
		if plan.ID == planID {
			plan.Visible = visible
			return s.store.UpsertPlanAs(principal, plan)
		}
	}
	return fmt.Errorf("%w: plan %q", core.ErrNotFound, planID)
}

func (s Service) DeletePlan(principal core.Principal, planID string) error {
	unlock := s.lockMutations()
	defer unlock()
	if err := s.requireAdmin(principal); err != nil {
		return err
	}
	err := s.store.DeletePlanAs(principal, planID)
	switch {
	case errors.Is(err, core.ErrNotFound):
		return fmt.Errorf("%w: plan %q", core.ErrNotFound, planID)
	case errors.Is(err, core.ErrConflict):
		return fmt.Errorf("%w: an organization or open checkout still uses this plan", core.ErrConflict)
	}
	return err
}

func (s Service) platformAccounts(principal core.Principal) ([]core.Account, error) {
	if err := s.requireAdmin(principal); err != nil {
		return nil, err
	}
	return s.store.PlatformAccounts(principal)
}

func (s Service) requireAdmin(principal core.Principal) error {
	if s.store == nil {
		return core.ErrUnauthorized
	}
	return s.store.AuthorizePlatformAdmin(principal)
}

func (s Service) lockMutations() func() {
	if s.mutationMu == nil {
		return func() {}
	}
	s.mutationMu.Lock()
	return s.mutationMu.Unlock
}

func validName(raw string) (string, error) {
	name := strings.TrimSpace(raw)
	if name == "" || !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl) || utf8.RuneCountInString(name) > 128 {
		return "", core.Invalid("names use 1-128 printable characters")
	}
	return name, nil
}

func validPlanKey(key string) bool {
	if len(key) == 0 || len(key) > 64 || key != strings.ToLower(key) {
		return false
	}
	for index, char := range key {
		valid := char >= 'a' && char <= 'z' || char >= '0' && char <= '9' || (index > 0 && (char == '-' || char == '_'))
		if !valid {
			return false
		}
	}
	return true
}

func adminFallback(value, fallback string) string {
	if value == "" {
		return fallback
	}
	return value
}

func planVisibilityValue(visible bool) string {
	if visible {
		return "false"
	}
	return "true"
}

func planVisibilityLabel(visible bool) string {
	if visible {
		return "Hide plan"
	}
	return "Show plan"
}
