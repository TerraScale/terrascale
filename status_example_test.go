package sdk_test

import (
	"context"
	"fmt"
	"log"

	sdk "terrascale.tech/sdk"
)

func ExampleStatusClient_Latency() {
	client, err := sdk.NewClient(sdk.Config{})
	if err != nil {
		log.Fatal(err)
	}
	latency, err := client.Status.Latency(context.Background(), "gru")
	if err != nil {
		log.Fatal(err)
	}
	for _, region := range latency.Regions {
		if region.Consensus != nil && region.Consensus.MedianMS != nil {
			fmt.Printf("%s: %d ms (%s)\n", region.Name, *region.Consensus.MedianMS, region.Status)
		}
	}
}
