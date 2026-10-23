package replbench

import "testing"

func TestLWWWinnerComparesStampThenNode(t *testing.T) {
	cases := []struct {
		writes map[string]Stamp
		want   string
	}{
		{map[string]Stamp{"gru": {HLC: 10, Node: 9}, "sjc": {HLC: 11, Node: 1}}, "sjc"},
		{map[string]Stamp{"gru": {HLC: 11, Node: 9}, "sjc": {HLC: 11, Node: 1}}, "gru"},
		{map[string]Stamp{"fra": {HLC: 5, Node: 0xffffffffffffffff}, "sjc": {HLC: 5, Node: 1}}, "fra"},
	}
	for _, c := range cases {
		if got := LWWWinner(c.writes); got != c.want {
			t.Errorf("LWWWinner(%v) = %s, want %s", c.writes, got, c.want)
		}
	}
	s, err := ParseStamp(FeedEvent{Version: "18446744073709551615", Origin: "ffffffffffffffff"})
	if err != nil || s.HLC != 1<<64-1 || s.Node != 1<<64-1 {
		t.Fatalf("ParseStamp = %v, %v", s, err)
	}
}
