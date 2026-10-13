// Package pitrbench verifies TerraBase continuous backup and point-in-time restore at scale: a seeded, deterministic
// document set (ids and bodies are functions of seed and index, so nothing per document is kept in memory), an
// fsynced acknowledgement ledger as the oracle, quiesced and unquiesced cuts, restores into new databases and a full,
// parallel merge-join of every restored document against the oracle with per-partition digests.
package pitrbench

import (
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"fmt"
	"math/rand/v2"
	"strconv"
	"time"

	"github.com/oklog/ulid/v2"
)

// IDEpochMs is the ULID time of document 0; document i has time IDEpochMs+i, so id order is index order.
const IDEpochMs = 1_760_000_000_000

// AcctValues is the cardinality of the indexed "acct" field: about 5,000 documents per value at 100M.
const AcctValues = 20011

// DocID is document i's id: a ULID with time IDEpochMs+i and entropy from SHA-256(seed, i).
func DocID(seed uint64, i int64) string {
	var in [16]byte
	binary.BigEndian.PutUint64(in[:8], seed)
	binary.BigEndian.PutUint64(in[8:], uint64(i))
	h := sha256.Sum256(in[:])
	return ulid.MustNew(uint64(IDEpochMs+i), bytes.NewReader(h[:10])).String()
}

// IndexOf is the document index an id encodes, or false when the id is not one this run generated.
func IndexOf(seed uint64, id string) (int64, bool) {
	u, err := ulid.ParseStrict(id)
	if err != nil || u.Time() < IDEpochMs {
		return 0, false
	}
	i := int64(u.Time() - IDEpochMs)
	return i, DocID(seed, i) == id
}

var (
	kinds    = [...]string{"order", "refund", "invoice", "shipment", "event"}
	statuses = [...]string{"open", "paid", "shipped", "closed"}
	regions  = [...]string{"fra", "gru", "sjc"}
	tagWords = [...]string{"alpha", "beta", "bulk", "gift", "priority", "retail", "returned", "wholesale"}
)

// Body is the canonical JSON (keys sorted at every depth, compact, ASCII only) of document i at revision rev (1 on
// insert, 2 after an update, 3 after a delete and re-create). About 410 bytes on average, 250 to 580: a representative
// order-like record with a nested object, an array, numbers, a boolean, a timestamp and a free-text note.
func Body(seed uint64, i int64, rev int) []byte {
	r := rand.New(rand.NewPCG(seed^uint64(rev)*0x9e3779b97f4a7c15, uint64(i)))
	b := make([]byte, 0, 600)
	b = append(b, `{"acct":"a-`...)
	b = appendPadded(b, i%AcctValues, 5)
	// Integer cents: a decimal like 1.50 could legitimately come back as 1.5.
	b = append(b, `","amountCents":`...)
	b = strconv.AppendInt(b, 100+r.Int64N(1_000_000)+int64(rev), 10)
	b = append(b, `,"createdAt":"`...)
	b = time.UnixMilli(IDEpochMs+i).UTC().AppendFormat(b, "2006-01-02T15:04:05.000Z")
	b = append(b, `","customer":{"id":"c-`...)
	b = appendPadded(b, (i*7919)%1_000_003, 7)
	b = append(b, `","region":"`...)
	b = append(b, regions[i%3]...)
	b = append(b, `","vip":`...)
	b = strconv.AppendBool(b, i%17 == 0)
	b = append(b, `},"kind":"`...)
	b = append(b, kinds[i%5]...)
	b = append(b, `","note":"`...)
	for range 32 + r.IntN(321) {
		b = append(b, byte('a'+r.IntN(26)))
	}
	b = append(b, `","qty":`...)
	b = strconv.AppendInt(b, i%50+1, 10)
	b = append(b, `,"rev":`...)
	b = strconv.AppendInt(b, int64(rev), 10)
	b = append(b, `,"seq":`...)
	b = strconv.AppendInt(b, i, 10)
	b = append(b, `,"status":"`...)
	b = append(b, statuses[(i+int64(rev))%4]...)
	b = append(b, `","tags":[`...)
	for t := range 2 + int(i%3) {
		if t > 0 {
			b = append(b, ',')
		}
		b = append(b, '"')
		b = append(b, tagWords[(i+int64(t)*3)%8]...)
		b = append(b, '"')
	}
	return append(b, "]}"...)
}

func appendPadded(b []byte, v int64, width int) []byte {
	s := strconv.FormatInt(v, 10)
	for range width - len(s) {
		b = append(b, '0')
	}
	return append(b, s...)
}

// ContentHash is SHA-256 of a canonical body.
func ContentHash(canonical []byte) [32]byte { return sha256.Sum256(canonical) }

// Acct is the indexed acct value of document i.
func Acct(i int64) string { return fmt.Sprintf("a-%05d", i%AcctValues) }
