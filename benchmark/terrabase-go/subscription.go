package terrabase

import (
	"context"
	"io"
	"sync/atomic"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

// Event is one frame of one backbone subscription stream: the initial
// confirmation, then ordered batches, content-free triggers, and the
// close frame. Exactly one accessor reports true.
type Event struct {
	confirmed    bool
	confirmation Confirmation
	batch        *Batch
	trigger      *Trigger
	closed       *Closed
}

// Confirmed reports the subscription confirmation: the first frame of
// every stream.
func (e Event) Confirmed() (Confirmation, bool) { return e.confirmation, e.confirmed }

// BatchEvent reports one ordered batch of change events.
func (e Event) BatchEvent() (Batch, bool) {
	if e.batch == nil {
		return Batch{}, false
	}
	return *e.batch, true
}

// TriggerEvent reports one content-free change trigger.
func (e Event) TriggerEvent() (Trigger, bool) {
	if e.trigger == nil {
		return Trigger{}, false
	}
	return *e.trigger, true
}

// ClosedEvent reports the subscription's close frame; the stream ends
// after it.
func (e Event) ClosedEvent() (Closed, bool) {
	if e.closed == nil {
		return Closed{}, false
	}
	return *e.closed, true
}

func eventFrom(frame *v1.SubscribeResponse) (Event, error) {
	if frame == nil {
		return Event{}, contractError("the engine sent a nil subscription frame")
	}
	switch typed := frame.GetEvent().(type) {
	case *v1.SubscribeResponse_Subscribed:
		if typed.Subscribed == nil {
			return Event{}, contractError("the engine sent an empty subscription confirmation")
		}
		return Event{confirmed: true, confirmation: Confirmation{
			SubscriptionID: typed.Subscribed.GetSubscriptionId(),
			StartSequence:  typed.Subscribed.GetStartSequence(),
		}}, nil
	case *v1.SubscribeResponse_Batch:
		if typed.Batch == nil || len(typed.Batch.GetEvents()) > maxBatchItems || typed.Batch.GetNextSequence() < typed.Batch.GetFromSequence() {
			return Event{}, contractError("the engine sent an invalid subscription batch")
		}
		batch := Batch{
			SubscriptionID: typed.Batch.GetSubscriptionId(),
			FromSequence:   typed.Batch.GetFromSequence(),
			NextSequence:   typed.Batch.GetNextSequence(),
		}
		var previous uint64
		for _, change := range typed.Batch.GetEvents() {
			if change == nil {
				return Event{}, contractError("the engine sent a nil subscription change")
			}
			kind, ok := changeKindFrom(change.GetKind())
			if !ok {
				return Event{}, contractError("the engine sent an unknown subscription change kind")
			}
			if err := boundName("subscription change collection", change.GetCollection(), maxCollectionBytes); err != nil {
				return Event{}, contractError("the engine sent an invalid subscription change collection")
			}
			if err := boundName("subscription change document", change.GetDocument(), maxDocumentBytes); err != nil {
				return Event{}, contractError("the engine sent an invalid subscription change document")
			}
			if err := optionalName("subscription change operation id", change.GetOperationId(), maxOperationBytes); err != nil {
				return Event{}, contractError("the engine sent an invalid subscription change operation id")
			}
			if change.GetSequence() < typed.Batch.GetFromSequence() || change.GetSequence() >= typed.Batch.GetNextSequence() || previous != 0 && change.GetSequence() <= previous {
				return Event{}, contractError("the engine sent subscription changes outside strict sequence order")
			}
			batch.Events = append(batch.Events, ChangeEvent{
				Sequence:    change.GetSequence(),
				OperationID: change.GetOperationId(),
				Kind:        kind,
				Collection:  change.GetCollection(),
				Document:    change.GetDocument(),
			})
			previous = change.GetSequence()
		}
		return Event{batch: &batch}, nil
	case *v1.SubscribeResponse_Trigger:
		if typed.Trigger == nil {
			return Event{}, contractError("the engine sent an empty subscription trigger")
		}
		if err := boundName("subscription trigger collection", typed.Trigger.GetCollection(), maxCollectionBytes); err != nil {
			return Event{}, contractError("the engine sent an invalid subscription trigger collection")
		}
		if err := optionalName("subscription trigger operation id", typed.Trigger.GetOperationId(), maxOperationBytes); err != nil {
			return Event{}, contractError("the engine sent an invalid subscription trigger operation id")
		}
		return Event{trigger: &Trigger{
			SubscriptionID: typed.Trigger.GetSubscriptionId(),
			Collection:     typed.Trigger.GetCollection(),
			Sequence:       typed.Trigger.GetSequence(),
			OperationID:    typed.Trigger.GetOperationId(),
		}}, nil
	case *v1.SubscribeResponse_Closed:
		if typed.Closed == nil {
			return Event{}, contractError("the engine sent an empty subscription close frame")
		}
		reason, ok := closeReasonFrom(typed.Closed.GetReason())
		if !ok {
			return Event{}, contractError("the engine sent an unknown subscription close reason")
		}
		return Event{closed: &Closed{
			SubscriptionID: typed.Closed.GetSubscriptionId(),
			Reason:         reason,
		}}, nil
	default:
		return Event{}, contractError("the engine sent a subscription frame outside the contract")
	}
}

// Subscription is one open backbone subscription: the server stream of
// ordered change batches plus the acknowledge and unsubscribe helpers
// that ride the sibling unary RPCs on the same channel.
//
// Recv must not be called concurrently; Acknowledge, Unsubscribe, and
// Close may.
type Subscription struct {
	client    *Client
	stream    v1.BackboneService_SubscribeClient
	ctx       context.Context
	cancel    context.CancelFunc
	id        atomic.Uint64
	confirmed bool
	closed    bool
}

// Subscribe opens one backbone subscription. The stream first delivers
// the confirmation, then ordered batches of change events, content-free
// triggers, and the close frame. subscriptionID is the caller-chosen
// identity of the subscription; collections bounds the stream to those
// collections (nil subscribes to every collection).
func (c *Client) Subscribe(ctx context.Context, subscriptionID uint64, collections []string, from Start) (*Subscription, error) {
	if len(collections) > maxCollections {
		return nil, localError("a subscription carries at most %d collections, got %d", maxCollections, len(collections))
	}
	for index, collection := range collections {
		if err := boundName("subscription collection", collection, maxCollectionBytes); err != nil {
			return nil, localError("subscription collection %d must be canonical text of 1..=%d bytes", index, maxCollectionBytes)
		}
	}
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return nil, err
	}
	streamCtx, cancel := context.WithCancel(ctx)
	stream, err := c.backbone.Subscribe(streamCtx, &v1.BackboneServiceSubscribeRequest{
		SubscriptionId: subscriptionID,
		Collections:    collections,
		From:           from.proto(),
	})
	if err != nil {
		cancel()
		return nil, interpret(err)
	}
	subscription := &Subscription{client: c, stream: stream, ctx: streamCtx, cancel: cancel}
	subscription.id.Store(subscriptionID)
	return subscription, nil
}

// ID is the subscription identity this stream serves.
func (s *Subscription) ID() uint64 { return s.id.Load() }

// Context is the subscription stream's own context: it is canceled by
// Close and by the end of the parent context of Subscribe.
func (s *Subscription) Context() context.Context { return s.ctx }

// Recv receives the next event of the stream. It blocks until an event
// arrives, the stream ends (io.EOF after the close frame or when the
// engine ends the stream), or the context is canceled. Errors are
// mapped into the typed error model.
func (s *Subscription) Recv() (Event, error) {
	frame, err := s.stream.Recv()
	if err != nil {
		if err == io.EOF {
			return Event{}, io.EOF
		}
		return Event{}, interpret(err)
	}
	event, err := eventFrom(frame)
	if err != nil {
		return Event{}, err
	}
	if confirmation, ok := event.Confirmed(); ok {
		if s.confirmed || s.closed || confirmation.SubscriptionID != s.id.Load() {
			return Event{}, contractError("the engine sent an invalid subscription confirmation sequence")
		}
		s.confirmed = true
		return event, nil
	}
	if !s.confirmed || s.closed {
		return Event{}, contractError("the engine sent a subscription frame outside its lifecycle")
	}
	var eventID uint64
	if batch, ok := event.BatchEvent(); ok {
		eventID = batch.SubscriptionID
	} else if trigger, ok := event.TriggerEvent(); ok {
		eventID = trigger.SubscriptionID
	} else if closed, ok := event.ClosedEvent(); ok {
		eventID = closed.SubscriptionID
		s.closed = true
	}
	if eventID != s.id.Load() {
		return Event{}, contractError("the engine sent a frame for another subscription")
	}
	return event, nil
}

// Acknowledge acknowledges the subscription through one backbone
// sequence: the engine releases its retention before it.
func (s *Subscription) Acknowledge(ctx context.Context, throughSequence uint64) (uint64, error) {
	response, err := s.client.backboneAcknowledge(ctx, s.id.Load(), throughSequence)
	if err != nil {
		return 0, err
	}
	return response, nil
}

// Unsubscribe closes the subscription on the engine side; the stream
// ends on its own.
func (s *Subscription) Unsubscribe(ctx context.Context) error {
	return s.client.backboneUnsubscribe(ctx, s.id.Load())
}

// Close cancels the subscription stream. It does not unsubscribe: an
// abandoned subscription is released by Unsubscribe or by the engine's
// disconnect handling.
func (s *Subscription) Close() error {
	s.cancel()
	return nil
}

// backboneAcknowledge maps Backbone/Acknowledge.
func (c *Client) backboneAcknowledge(ctx context.Context, subscriptionID, throughSequence uint64) (uint64, error) {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return 0, err
	}
	response, err := c.backbone.Acknowledge(ctx, &v1.BackboneServiceAcknowledgeRequest{
		SubscriptionId:  subscriptionID,
		ThroughSequence: throughSequence,
	})
	if err != nil {
		return 0, interpret(err)
	}
	if response == nil || response.GetSubscriptionId() != subscriptionID || response.GetAcknowledgedThrough() > throughSequence {
		return 0, contractError("the engine returned an invalid subscription acknowledgement")
	}
	return response.GetAcknowledgedThrough(), nil
}

// backboneUnsubscribe maps Backbone/Unsubscribe.
func (c *Client) backboneUnsubscribe(ctx context.Context, subscriptionID uint64) error {
	ctx, err := c.outgoing(ctx, false, "")
	if err != nil {
		return err
	}
	response, err := c.backbone.Unsubscribe(ctx, &v1.BackboneServiceUnsubscribeRequest{SubscriptionId: subscriptionID})
	if err != nil {
		return interpret(err)
	}
	if response == nil || response.GetSubscriptionId() != subscriptionID {
		return contractError("the engine returned an invalid unsubscribe response")
	}
	return nil
}
