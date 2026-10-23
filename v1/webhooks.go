package v1

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"time"
)

// Webhook is a collection change endpoint. Status is active or feed_gap.
// A feed_gap requires reconciling current data, deleting and recreating the hook.
type Webhook struct {
	ID        string    `json:"id" ulid:"required"`
	URL       string    `json:"url"`
	CreatedAt time.Time `json:"createdAt"`
	Status    string    `json:"status"`
}

// CreatedWebhook includes the signing secret returned only at creation.
// Store Secret securely now; never log it. A lost secret requires recreation.
type CreatedWebhook struct {
	Webhook
	Secret string `json:"secret"`
}

func (CreatedWebhook) String() string                  { return "terrabase webhook (secret redacted)" }
func (hook CreatedWebhook) Format(f fmt.State, _ rune) { fmt.Fprint(f, hook.String()) }

// WebhookDelivery is a retained delivery attempt record, without its body or secrets.
// Status is pending or delivered. ErrorCode is nil, http_error, transport_error,
// or target_rejected. At most the latest 100 records per webhook are retained.
type WebhookDelivery struct {
	ID            string     `json:"id" ulid:"required"`
	CreatedAt     time.Time  `json:"createdAt"`
	Status        string     `json:"status"`
	Attempts      int64      `json:"attempts"`
	LastAttemptAt *time.Time `json:"lastAttemptAt"`
	NextAttemptAt *time.Time `json:"nextAttemptAt"`
	HTTPStatus    *int       `json:"httpStatus"`
	ErrorCode     *string    `json:"errorCode"`
}

// WebhookListOptions pages webhooks or deliveries in ascending ULID order.
// After is the last item's ID; an empty result ends pagination.
// Limit is 1..100; zero uses the server default of 50.
type WebhookListOptions struct {
	After string
	Limit int
}

func (o WebhookListOptions) query() (url.Values, error) {
	if o.Limit < 0 || o.Limit > 100 {
		return nil, errors.New("terrabase: webhook limit must be 1..100 or zero for the default")
	}
	q := url.Values{}
	if o.After != "" {
		if err := checkID(o.After); err != nil {
			return nil, err
		}
		q.Set("after", o.After)
	}
	if o.Limit != 0 {
		q.Set("limit", strconv.Itoa(o.Limit))
	}
	return q, nil
}

// CreateWebhook registers an HTTPS endpoint from the current feed head, without
// backfill. The producer validates the URL and public destination on every attempt.
// Needs both documents:read and documents:write. Creation is never automatically retried after uncertainty.
func (col *Collection) CreateWebhook(ctx context.Context, endpoint string) (*CreatedWebhook, error) {
	p, err := col.path("webhooks")
	if err != nil {
		return nil, err
	}
	var out CreatedWebhook
	if _, err := col.db.c.call(ctx, request{method: http.MethodPost, path: p}, struct {
		URL string `json:"url"`
	}{endpoint}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// Webhooks lists one page of endpoints without secrets. Needs documents:read.
func (col *Collection) Webhooks(ctx context.Context, opts WebhookListOptions) ([]Webhook, error) {
	q, err := opts.query()
	if err != nil {
		return nil, err
	}
	p, err := col.path("webhooks")
	if err != nil {
		return nil, err
	}
	var out []Webhook
	if _, err := col.db.c.call(ctx, request{method: http.MethodGet, path: p, query: q, replaySafe: true}, nil, &out); err != nil {
		return nil, err
	}
	return out, nil
}

// DeleteWebhook stops future attempts; an in-flight request may complete.
// An unknown ID returns 404. Needs both documents:read and documents:write.
func (col *Collection) DeleteWebhook(ctx context.Context, id string) error {
	if err := checkID(id); err != nil {
		return err
	}
	p, err := col.path("webhooks", id)
	if err != nil {
		return err
	}
	_, err = col.db.c.call(ctx, request{method: http.MethodDelete, path: p}, nil, nil)
	return err
}

// WebhookDeliveries lists one page of retained delivery records. Needs documents:read.
func (col *Collection) WebhookDeliveries(ctx context.Context, id string, opts WebhookListOptions) ([]WebhookDelivery, error) {
	if err := checkID(id); err != nil {
		return nil, err
	}
	q, err := opts.query()
	if err != nil {
		return nil, err
	}
	p, err := col.path("webhooks", id, "deliveries")
	if err != nil {
		return nil, err
	}
	var out []WebhookDelivery
	if _, err := col.db.c.call(ctx, request{method: http.MethodGet, path: p, query: q, replaySafe: true}, nil, &out); err != nil {
		return nil, err
	}
	return out, nil
}
