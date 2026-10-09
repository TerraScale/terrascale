package usage

import (
	"net/url"
	"strconv"
)

// Compare submitted values with the page's initial values, not the store's
// current values. Untouched controls must not overwrite another admin's edits
// or require resources deleted after this page was loaded to still exist.
func changedFormCap(values url.Values, key string) (int64, bool, bool) {
	value, present, valid := formCap(values, key)
	initial, hasInitial, initialValid := formCap(values, "initial_"+key)
	return value, present && (!hasInitial || value != initial), valid && initialValid
}

func changedPaidExcess(values url.Values) (*bool, bool) {
	if _, hasOrganizationCap := values["spendCap"]; !hasOrganizationCap {
		return nil, true
	}
	paid := false
	if submitted, exists := values["paidExcess"]; exists {
		if len(submitted) != 1 {
			return nil, false
		}
		switch submitted[0] {
		case "true", "on":
			paid = true
		case "false", "":
		default:
			return nil, false
		}
	}
	if initial, exists := values["initial_paidExcess"]; exists {
		if len(initial) != 1 || (initial[0] != "true" && initial[0] != "false") {
			return nil, false
		}
		if initial[0] == strconv.FormatBool(paid) {
			return nil, true
		}
	}
	return &paid, true
}

func changedCapsFromForm(values url.Values, prefix string) (map[string]int64, bool) {
	changes, valid := capsFromForm(values, prefix)
	initial, initialValid := capsFromForm(values, "initial_"+prefix)
	if !valid || !initialValid {
		return nil, false
	}
	for id, value := range changes {
		if original, exists := initial[id]; exists && value == original {
			delete(changes, id)
		}
	}
	return changes, true
}

func changedOperationsFromForm(values url.Values, prefix string) (map[string]operationLimitChange, bool) {
	changes, valid := operationsFromForm(values, prefix)
	initial, initialValid := operationsFromForm(values, "initial_"+prefix)
	if !valid || !initialValid {
		return nil, false
	}
	for id, change := range changes {
		original := initial[id]
		omitUnchanged := func(value **int64, before *int64) {
			if *value != nil && before != nil && **value == *before {
				*value = nil
			}
		}
		omitUnchanged(&change.ReadLimit, original.ReadLimit)
		omitUnchanged(&change.WriteLimit, original.WriteLimit)
		omitUnchanged(&change.ReplicationLimit, original.ReplicationLimit)
		if change.ReadLimit == nil && change.WriteLimit == nil && change.ReplicationLimit == nil {
			delete(changes, id)
		} else {
			changes[id] = change
		}
	}
	return changes, true
}
