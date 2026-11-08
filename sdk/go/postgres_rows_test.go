package sdk

import "testing"

func TestPostgresRows(t *testing.T) {
	fields := postgresFields(20, 25)
	rows := [][][]byte{
		{[]byte("1"), []byte("alpha")},
		{[]byte("2"), []byte("beta")},
		{[]byte("3"), []byte("gamma")},
	}
	client := postgresFixtureClient(t, postgresFixture{fields: fields, rows: rows, tag: "SELECT 3"})
	stream, err := client.QueryRows(t.Context(), PostgresQuery{Statement: "SELECT id, name FROM items"})
	if err != nil {
		t.Fatal(err)
	}
	var ids []int64
	var names []string
	for stream.Next() {
		values := stream.Values()
		id, ok := values[0].Int8()
		name, _ := values[1].Text()
		if !ok || len(values) != 2 {
			t.Fatalf("row %d: id=%v values=%d", len(ids), values[0].Value(), len(values))
		}
		ids = append(ids, id)
		names = append(names, name)
	}
	if stream.Err() != nil {
		t.Fatal(stream.Err())
	}
	if stream.CommandTag() != "SELECT 3" {
		t.Fatalf("tag=%q", stream.CommandTag())
	}
	if len(ids) != 3 || ids[0] != 1 || ids[2] != 3 || names[1] != "beta" {
		t.Fatalf("ids=%v names=%v", ids, names)
	}
	if err := stream.Close(); err != nil {
		t.Fatal(err)
	}
	result, err := client.Query(t.Context(), PostgresQuery{Statement: "SELECT 1"})
	if err != nil || result.CommandTag != "SELECT 3" {
		t.Fatalf("gate release: %v %q", err, result.CommandTag)
	}
}
