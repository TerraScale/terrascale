package apikeys

import (
	"slices"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

type statusChoice struct{ Value, Label string }

// credentialTab is the single configuration for a tab's list and controls.
// Both credential kinds currently support status and project database filters.
type credentialTab struct {
	Kind, CredentialKind, Label, SearchLabel, CreateLabel, CreateDialog string
	Statuses                                                            []statusChoice
	DatabaseFilter                                                      bool
}

var credentialStatuses = []statusChoice{{Value: "active", Label: "Active"}, {Value: "inactive", Label: "Inactive"}}

var credentialTabs = []credentialTab{
	{Label: "All credentials", SearchLabel: "Search credentials", CreateLabel: "Create credential", CreateDialog: "create-credential", Statuses: credentialStatuses, DatabaseFilter: true},
	{Kind: "key", CredentialKind: kindAPIKey, Label: "API keys", SearchLabel: "Search API keys", CreateLabel: "Create key", CreateDialog: "create-key", Statuses: credentialStatuses, DatabaseFilter: true},
	{Kind: "m2m", CredentialKind: kindM2M, Label: "M2M clients", SearchLabel: "Search M2M clients", CreateLabel: "Create client", CreateDialog: "create-m2m", Statuses: credentialStatuses, DatabaseFilter: true},
}

func controlsFor(kind string) credentialTab {
	for _, tab := range credentialTabs {
		if tab.Kind == kind {
			return tab
		}
	}
	return credentialTabs[0]
}

// forTab keeps compatible filters and drops those the destination cannot use.
func (f listFilters) forTab(kind string) listFilters {
	tab := controlsFor(kind)
	f.Kind = tab.Kind
	if !slices.ContainsFunc(tab.Statuses, func(status statusChoice) bool { return status.Value == f.Status }) {
		f.Status = ""
	}
	if !tab.DatabaseFilter {
		f.DatabaseID = ""
	}
	return f
}

// forProject clears database selections left over from another project.
func (f listFilters) forProject(project core.Project) listFilters {
	f = f.forTab(f.Kind)
	if _, ok := project.Databases[f.DatabaseID]; !ok {
		f.DatabaseID = ""
	}
	return f
}

func (f listFilters) tabURL(kind string) string {
	return f.forTab(kind).with("/api-keys")
}

func (f listFilters) clearURL() string {
	return (listFilters{Kind: f.Kind}).with("/api-keys")
}

func (f listFilters) tabs() []ui.Tab {
	tabs := make([]ui.Tab, 0, len(credentialTabs))
	for _, tab := range credentialTabs {
		tabs = append(tabs, ui.Tab{Label: tab.Label, URL: f.tabURL(tab.Kind), Active: f.Kind == tab.Kind})
	}
	return tabs
}

func (f listFilters) createURL(kind string) string {
	query := f.query()
	query.Set("create", kind)
	return "/api-keys?" + query.Encode()
}
