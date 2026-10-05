package core

import "sort"

// RepairSelections removes lost memberships and picks a stable fallback for
// a deleted organization or project. Persistent stores apply it on reads;
// the demo store applies it when publishing a mutation.
func RepairSelections(a *Account) {
	for id, org := range a.Organizations {
		if !MemberActive(org, a.User.ID) {
			delete(a.Organizations, id)
		}
	}
	if a.ActiveOrganizationID == "" {
		return
	}
	if _, exists := a.Organizations[a.ActiveOrganizationID]; !exists {
		ids := make([]string, 0, len(a.Organizations))
		for id := range a.Organizations {
			ids = append(ids, id)
		}
		sort.Slice(ids, func(i, j int) bool {
			x, y := a.Organizations[ids[i]], a.Organizations[ids[j]]
			if (x.Kind == "personal") != (y.Kind == "personal") {
				return x.Kind == "personal"
			}
			xTime, yTime := x.Members[a.User.ID].CreatedAt, y.Members[a.User.ID].CreatedAt
			if !xTime.Equal(yTime) {
				return xTime.Before(yTime)
			}
			return ids[i] < ids[j]
		})
		a.ActiveOrganizationID, a.ActiveProjectID = "", ""
		if len(ids) > 0 {
			a.ActiveOrganizationID = ids[0]
			a.ActiveProjectID = a.Organizations[ids[0]].DefaultProjectID()
		}
	}
	if org, exists := a.Organizations[a.ActiveOrganizationID]; exists && a.ActiveProjectID != "" {
		if project, exists := org.Projects[a.ActiveProjectID]; !exists || project.OrganizationID != org.ID {
			a.ActiveProjectID = org.DefaultProjectID()
		}
	}
}
