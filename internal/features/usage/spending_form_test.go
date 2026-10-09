package usage

import (
	"net/url"
	"testing"
)

func TestSpendingFormIncludesOnlyEditedValues(t *testing.T) {
	values := url.Values{
		"spendCap": {"10.0"}, "initial_spendCap": {"10.00"},
		"initial_paidExcess": {"false"},
		"projCap_untouched":  {"0"}, "initial_projCap_untouched": {""},
		"projCap_edited": {"20"}, "initial_projCap_edited": {"10.00"},
		"projRead_edited": {"100"}, "initial_projRead_edited": {"0100"},
		"projWrite_edited": {""}, "initial_projWrite_edited": {"500"},
		"projReplication_edited": {"200"}, "initial_projReplication_edited": {""},
		"dbCap_deleted": {"10"}, "initial_dbCap_deleted": {"10.00"},
		"dbRead_deleted": {"0"}, "initial_dbRead_deleted": {""},
	}
	if _, changed, valid := changedFormCap(values, "spendCap"); !valid || changed {
		t.Fatal("an unchanged organization cap was included")
	}
	if paid, valid := changedPaidExcess(values); !valid || paid != nil {
		t.Fatal("an unchanged excess setting was included")
	}
	caps, valid := changedCapsFromForm(values, "projCap_")
	if !valid || len(caps) != 1 || caps["edited"] != 2000 {
		t.Fatalf("project price changes = %v, valid = %t", caps, valid)
	}
	operations, valid := changedOperationsFromForm(values, "proj")
	change := operations["edited"]
	if !valid || len(operations) != 1 || change.ReadLimit != nil || change.WriteLimit == nil || *change.WriteLimit != 0 || change.ReplicationLimit == nil || *change.ReplicationLimit != 200 {
		t.Fatalf("operation changes = %+v, valid = %t", change, valid)
	}
	if caps, valid := changedCapsFromForm(values, "dbCap_"); !valid || len(caps) != 0 {
		t.Fatal("an unchanged deleted database's price was included")
	}
	if operations, valid := changedOperationsFromForm(values, "db"); !valid || len(operations) != 0 {
		t.Fatal("an unchanged deleted database's operations were included")
	}
	values.Set("paidExcess", "true")
	if paid, valid := changedPaidExcess(values); !valid || paid == nil || !*paid {
		t.Fatal("changing excess without changing the price was omitted")
	}
	values.Del("paidExcess")
	values.Set("initial_paidExcess", "true")
	if paid, valid := changedPaidExcess(values); !valid || paid == nil || *paid {
		t.Fatal("disabling excess was omitted")
	}
}

func TestSpendingFormRejectsMalformedInitialValuesAndSupportsLegacyForms(t *testing.T) {
	if _, _, valid := changedFormCap(url.Values{"spendCap": {"1"}, "initial_spendCap": {"1", "2"}}, "spendCap"); valid {
		t.Fatal("accepted duplicate initial price values")
	}
	if _, valid := changedCapsFromForm(url.Values{"initial_dbCap_db": {"-1"}}, "dbCap_"); valid {
		t.Fatal("accepted invalid initial database price")
	}
	if _, valid := changedOperationsFromForm(url.Values{"initial_projRead_p": {"1.5"}}, "proj"); valid {
		t.Fatal("accepted invalid initial operation limit")
	}
	if _, valid := changedPaidExcess(url.Values{"spendCap": {"1"}, "initial_paidExcess": {"invalid"}}); valid {
		t.Fatal("accepted invalid initial excess setting")
	}
	values := url.Values{"spendCap": {"10"}, "projCap_p": {"20"}, "projRead_p": {"100"}}
	if price, changed, valid := changedFormCap(values, "spendCap"); !valid || !changed || price != 1000 {
		t.Fatal("a legacy organization cap was ignored")
	}
	if caps, valid := changedCapsFromForm(values, "projCap_"); !valid || caps["p"] != 2000 {
		t.Fatal("a legacy project cap was ignored")
	}
	if operations, valid := changedOperationsFromForm(values, "proj"); !valid || operations["p"].ReadLimit == nil || *operations["p"].ReadLimit != 100 {
		t.Fatal("a legacy operation limit was ignored")
	}
}
