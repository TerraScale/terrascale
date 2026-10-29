// Installed-module journey; the producer owns the real signed TLS listener.
package main

import (
	"context"
	"errors"
	"fmt"
	"math"
	"os"
	"strconv"
	"time"

	sdk "terrascale.tech/sdk"
)

func required(name string) string {
	value := os.Getenv(name)
	if value == "" {
		panic("missing " + name)
	}
	return value
}
func identifier(name string) string {
	value := required(name)
	for index, character := range value {
		if !(character >= 'a' && character <= 'z' || character == '_' || index > 0 && character >= '0' && character <= '9') {
			panic("invalid " + name)
		}
	}
	return `"` + value + `"`
}
func check(value bool) {
	if !value {
		panic("PostgreSQL journey assertion failed")
	}
}

func main() {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	port, err := strconv.ParseUint(required("TERRASCALE_PG_PORT"), 10, 16)
	if err != nil {
		panic("invalid TERRASCALE_PG_PORT")
	}
	certificate, err := os.ReadFile(required("TERRASCALE_PG_CA_FILE"))
	if err != nil {
		panic("cannot read TERRASCALE_PG_CA_FILE")
	}
	client, err := sdk.ConnectPostgres(ctx, sdk.PostgresConfig{Host: required("TERRASCALE_PG_HOST"), Port: uint16(port), TLSServerName: required("TERRASCALE_PG_TLS_SERVER_NAME"),
		CACertificatePEM: certificate, Database: required("TERRASCALE_PG_DATABASE"), KeyID: required("TERRASCALE_PG_KEY_ID"), Password: required("TERRASCALE_PG_PASSWORD")})
	if err != nil {
		panic(err)
	}
	defer client.Close(ctx)
	table, key, payloadColumn := identifier("TERRASCALE_PG_TABLE"), identifier("TERRASCALE_PG_KEY_COLUMN"), identifier("TERRASCALE_PG_PAYLOAD_COLUMN")
	query := func(statement string, parameters ...sdk.PostgresValue) sdk.PostgresResult {
		result, err := client.Query(ctx, sdk.PostgresQuery{Statement: statement, Parameters: parameters})
		if err != nil {
			panic(err)
		}
		return result
	}
	payload := "quote ' ; DROP TABLE sentinel; -- \\ Unicode α 🌍"
	query("BEGIN")
	inserted := query("INSERT INTO "+table+" ("+key+","+payloadColumn+") VALUES ($1,$2),($3,$4)", sdk.PostgresInt8(math.MinInt64), sdk.PostgresText(payload), sdk.PostgresInt8(math.MaxInt64), sdk.PostgresText("maximum"))
	check(inserted.AffectedRows == 2)
	staged := query("SELECT "+payloadColumn+","+key+" FROM "+table+" WHERE "+key+"=$1", sdk.PostgresInt8(math.MinInt64))
	check(len(staged.Rows) == 1 && len(staged.Rows[0]) == 2)
	text, textOK := staged.Rows[0][0].Text()
	integer, intOK := staged.Rows[0][1].Int8()
	check(textOK && intOK && text == payload && integer == math.MinInt64 && staged.Columns[0].OID == 25 && staged.Columns[1].OID == 20)
	query("COMMIT")
	committed := query("SELECT "+key+","+payloadColumn+" FROM "+table+" WHERE "+key+"=$1", sdk.PostgresInt8(math.MaxInt64))
	check(len(committed.Rows) == 1)
	integer, intOK = committed.Rows[0][0].Int8()
	check(intOK && integer == math.MaxInt64)
	_, err = client.Query(ctx, sdk.PostgresQuery{Statement: "SELECT " + key + " FROM " + table})
	var denied *sdk.PostgresError
	check(errors.As(err, &denied) && denied.SQLState == "0A000")
	for _, integer := range []int64{math.MinInt64, math.MaxInt64} {
		check(query("DELETE FROM "+table+" WHERE "+key+"=$1", sdk.PostgresInt8(integer)).AffectedRows == 1)
	}
	fmt.Println("PostgreSQL installed-module point journey passed")
}
