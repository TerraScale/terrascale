package terrabase_test

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"

	terrabase "terrascale.tech/terrabase-go"
)

// Example walks one full round trip against the engine: connect over
// mutually authenticated TLS, bind the channel identity, write one
// document, read it back, query the collection, and stream its
// backbone changes. It is compiled but not executed by `go test`: it
// needs a live engine, which the integration lane of
// integration_test.go drives.
func Example() {
	// 1. Connect: production lanes are mutually authenticated TLS —
	// the operator-provisioned root bundle and the client certificate
	// pair. The transport authenticates the channel; Bind then names
	// the application identity within it.
	root, err := os.ReadFile("client-root.pem")
	if err != nil {
		log.Fatal(err)
	}
	cert, err := os.ReadFile("client-cert.pem")
	if err != nil {
		log.Fatal(err)
	}
	key, err := os.ReadFile("client-key.pem")
	if err != nil {
		log.Fatal(err)
	}
	client, err := terrabase.Dial("engine.terrabase.internal:443",
		terrabase.WithRootCerts(root),
		terrabase.WithClientCert(cert, key),
	)
	if err != nil {
		log.Fatal(err)
	}
	defer client.Close()

	ctx := context.Background()

	// 2. Bind the channel: every later RPC carries this identity.
	binding, err := client.Bind(ctx, "orders-api", "tenant-a")
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("bound to serving instance", binding.ServingInstance)

	// 3. Write one document. The expected-version discipline is the
	// optimistic-concurrency compare; the idempotency key defaults to
	// the operation identity, so a retry of op-write-42 deduplicates.
	outcome, err := client.DocumentWrite(ctx,
		terrabase.DocumentPut{
			Collection: "books",
			ID:         "ddia",
			Body: terrabase.Value(map[string]terrabase.Value{
				"title": "Designing Data-Intensive Applications",
				"pages": 616,
				"tags":  terrabase.Value([]terrabase.Value{"distributed", "storage"}),
			}),
		},
		terrabase.ExpectAbsent(), // create only: refuse if it exists
		terrabase.AckRegionalQuorum,
		"op-write-42",
	)
	if err != nil {
		log.Fatal(err)
	}
	// Conflicts and refusals are outcomes, not errors.
	if receipt, committed := outcome.Committed(); committed {
		fmt.Println("committed at sequence", receipt.Sequence, "version", receipt.Version)
	} else if outcome.Conflict() {
		fmt.Println("lost the expected-version race")
	} else if reason, refused := outcome.Refused(); refused {
		fmt.Println("refused:", reason)
	}

	// 4. Read it back.
	document, found, err := client.DocumentRead(ctx, "books", "ddia", terrabase.ReadLevelRegionalQuorum)
	if err != nil {
		log.Fatal(err)
	}
	if found {
		fmt.Printf("read %s/%s at version %d\n", document.Collection, document.ID, document.Version)
	}

	// 5. Query the collection.
	result, err := client.QueryExecute(ctx, 1, "from books where pages > 500", 100, terrabase.ReadLevelDefault)
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("query matched", result.Count, "documents, truncated:", result.Truncated)

	// 6. Subscribe to the collection's backbone changes: the stream
	// confirms, then delivers ordered batches to acknowledge.
	subscription, err := client.Subscribe(ctx, 1, []string{"books"}, terrabase.FromLive())
	if err != nil {
		log.Fatal(err)
	}
	defer subscription.Close()
	for {
		event, err := subscription.Recv()
		if errors.Is(err, context.Canceled) || err != nil {
			break // the stream ended: shutdown, close frame, or teardown
		}
		if confirmation, ok := event.Confirmed(); ok {
			fmt.Println("subscribed from sequence", confirmation.StartSequence)
			continue
		}
		if batch, ok := event.BatchEvent(); ok {
			for _, change := range batch.Events {
				fmt.Printf("change %d: %s %s/%s\n", change.Sequence, change.Kind, change.Collection, change.Document)
			}
			if _, err := subscription.Acknowledge(ctx, batch.NextSequence-1); err != nil {
				log.Fatal(err)
			}
		}
	}
}
