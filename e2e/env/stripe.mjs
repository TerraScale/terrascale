import { createHmac, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';

const apiVersion = '2026-09-30.endive';
const endpoint = 'https://dash.terrascale.tech/api/v1/billing/stripe/webhook';
const objects = new Map();
const checkouts = new Map();
const replays = new Map();
const failures = new Map();
const deliveredEvents = new Map();
const authFailures = new Set();
const refundStatuses = new Map();
const subscriptionTaxUpdates = new Map();
const log = { calls: [], events: [], cancels: [], refunds: [], unexpected: [] };
const holds = new Map();
let billingClock = null;

const now = () => Math.floor(Date.now() / 1000);
const hour = 3600;
const within = (earliest, latest) => (value) => value === null || (/^\d+$/.test(value) && Number(value) >= now() + earliest && Number(value) <= now() + latest);
const id = (prefix) => `${prefix}_${randomBytes(12).toString('hex')}`;
const list = (data) => ({ data, has_more: false });
const pageSize = 2;
const paged = (data, query, at) => {
  const ordered = [...data].sort((left, right) => at(right) - at(left) || (left.id < right.id ? 1 : -1));
  const after = query.get('starting_after');
  const start = after ? ordered.findIndex((item) => item.id === after) + 1 : 0;
  const size = Math.min(Number(query.get('limit') ?? 10), pageSize);
  return { object: 'list', data: ordered.slice(start, start + size), has_more: start + size < ordered.length };
};
const inRange = (query, value) => (!query.has('created[gte]') || value >= Number(query.get('created[gte]'))) && (!query.has('created[lte]') || value <= Number(query.get('created[lte]')));
const deferred = () => { let resolve; const promise = new Promise((done) => { resolve = done; }); return { promise, resolve }; };
const record = (type, created, object) => store({ id: id('evt'), object: 'event', type, created, data: { object: structuredClone(object) } });
const store = (object) => (objects.set(object.id, object), object);
const find = (prefix, key) => (key?.startsWith(`${prefix}_`) ? objects.get(key) : undefined);
const owned = (prefix, customer) => [...objects.values()].filter((object) => object.id.startsWith(`${prefix}_`) && object.customer === customer);
const keys = (query) => [...new Set(query.keys())].filter((key) => !key.startsWith('expand[')).sort();
const customerId = /^cus_\w+$/;
const secret = (prefix) => readFileSync(`${process.env.E2E_STATE}/secrets`, 'utf8').split('\n').find((line) => line.startsWith(prefix));
const fields = (form, prefix) =>
  Object.fromEntries([...form].filter(([key]) => key.startsWith(`${prefix}[`) && key.indexOf('[', prefix.length + 1) < 0).map(([key, value]) => [key.slice(prefix.length + 1, -1), value]));

for (const plan of JSON.parse(readFileSync(`${process.env.E2E_BUILD}/dashboard/Common/Accounts/pricing.json`, 'utf8')).plans) {
  if (!plan.available || plan.billingIntervals.length === 0) continue;
  const product = store({ id: `prod_${plan.id}`, active: true, metadata: { plan: plan.id }, name: `TerraScale ${plan.name}` });
  for (const interval of plan.billingIntervals) {
    const unit_amount = Math.round((interval === 'year' ? plan.priceAnnualUsd : plan.priceMonthlyUsd) * 100);
    store({ id: `price_${plan.id}_${interval}`, active: true, currency: 'usd', product: product.id, recurring: { interval, interval_count: 1, usage_type: 'licensed' }, unit_amount });
  }
}

function expand(value, [head, ...rest]) {
  if (Array.isArray(value)) return value.map((item) => expand(item, [head, ...rest]));
  const found = typeof value === 'string' && objects.has(value) ? structuredClone(objects.get(value)) : value;
  if (head === undefined || found === null || typeof found !== 'object') return found;
  if (head === 'tax_ids' && rest.length === 0 && found.id.startsWith('cus_')) return { ...found, tax_ids: list([]) };
  return head in found ? { ...found, [head]: expand(found[head], rest) } : found;
}

function render(object, query) {
  return [...query].filter(([key]) => /^expand\[\d+\]$/.test(key)).reduce((value, [, path]) => expand(value, path.split('.')), structuredClone(object));
}

const api = [
  ['GET', /^\/v1\/prices\/(price_\w+)$/, (query, [price]) => find('price', price), {}, {}, ['expand']],
  ['GET', /^\/v1\/events$/, (query) => {
    const types = [...query.entries()].filter(([key]) => key.startsWith('types[')).map(([, value]) => value);
    return paged([...objects.values()].filter((object) => object.object === 'event' && types.includes(object.type) && inRange(query, object.created)), query, (event) => event.created);
  }, {}, {}, ['types', 'created', 'limit', 'starting_after']],
  ['GET', /^\/v1\/prices$/, () => list([...objects.values()].filter((object) => object.id.startsWith('price_'))), {}, {}, ['active', 'currency', 'type', 'limit', 'expand', 'product']],
  ['POST', /^\/v1\/customers$/, (form) => store({ id: id('cus'), metadata: fields(form, 'metadata'), name: form.get('name'), email: form.get('email') }), {}, {}, ['name', 'email', 'metadata']],
  ['GET', /^\/v1\/customers\/search$/, (query) => {
    const organization = /^metadata\['organization_id'\]:'([0-9A-Z]{26})'$/.exec(query.get('query') ?? '')?.[1];
    if (!organization) return undefined;
    const customers = [...objects.values()].filter((object) => object.id.startsWith('cus_') && object.metadata.organization_id === organization);
    return { data: customers, has_more: false, next_page: null };
  }, {}, {}, ['query', 'limit', 'page']],
  ['GET', /^\/v1\/customers\/(cus_\w+)$/, (query, [customer]) => find('cus', customer), {}, {}, ['expand']],
  ['POST', /^\/v1\/customers\/(cus_\w+)$/, (form, [customer]) => {
    const found = find('cus', customer);
    return found && Object.assign(found, { metadata: { ...found.metadata, ...fields(form, 'metadata') } });
  }, {}, {}, ['metadata']],
  ['POST', /^\/v1\/checkout\/sessions$/, (form) => {
    const price = find('price', form.get('line_items[0][price]'));
    if (form.get('mode') !== 'subscription' || !price || form.get('line_items[0][quantity]') !== '1' || !find('cus', form.get('customer'))) return undefined;
    const session = store({
      object: 'checkout.session', id: id('cs_test'), client_reference_id: form.get('client_reference_id'), customer: form.get('customer'),
      metadata: fields(form, 'metadata'), mode: 'subscription', status: 'open', url: null, automatic_tax: { enabled: form.get('automatic_tax[enabled]') === 'true' },
    });
    session.billing_address_collection = form.get('billing_address_collection');
    session.customer_update = fields(form, 'customer_update');
    session.url = `https://checkout.stripe.com/c/pay/${session.id}`;
    checkouts.set(session.id, { price, metadata: fields(form, 'subscription_data[metadata]'), success: form.get('success_url'), cancel: form.get('cancel_url') });
    return session;
  }, {}, {
    expires_at: within(hour / 2, 24 * hour),
    billing_address_collection: (value) => value === 'required',
    'customer_update[address]': (value) => value === 'auto',
    'customer_update[name]': (value) => value === 'auto',
    'automatic_tax[enabled]': (value) => value === 'true',
    'allowed_payment_method_types[0]': (value) => value === 'card',
    'allowed_payment_method_types[1]': (value) => value === 'link',
    'allowed_payment_method_types[2]': (value) => value === null,
  }, ['mode', 'customer', 'client_reference_id', 'success_url', 'cancel_url', 'expires_at', 'billing_address_collection', 'payment_method_collection', 'allowed_payment_method_types', 'line_items', 'metadata', 'tax_id_collection', 'customer_update', 'subscription_data', 'automatic_tax']],
  ['GET', /^\/v1\/checkout\/sessions$/, (query) => list(owned('cs', query.get('customer'))), { customer: customerId }, {}, ['customer', 'limit', 'starting_after']],
  ['GET', /^\/v1\/checkout\/sessions\/(cs_test_\w+)$/, (query, [session]) => find('cs', session), {}, {}, ['expand']],
  ['POST', /^\/v1\/checkout\/sessions\/(cs_test_\w+)\/expire$/, (form, [session]) => {
    const found = find('cs', session);
    return found?.status === 'open' ? Object.assign(found, { status: 'expired', url: null }) : found;
  }, {}, {}, []],
  ['GET', /^\/v1\/subscriptions$/, (query) => list(owned('sub', query.get('customer'))), { customer: customerId, status: /^all$/ }, {}, ['customer', 'status', 'limit', 'starting_after']],
  ['GET', /^\/v1\/subscriptions\/(sub_\w+)$/, (query, [subscription]) => find('sub', subscription), {}, {}, ['expand']],
  ['POST', /^\/v1\/subscriptions\/(sub_\w+)$/, (form, [subscription]) => {
    const found = find('sub', subscription);
    if (found && form.has('automatic_tax[enabled]')) {
      found.automatic_tax = { enabled: true };
      subscriptionTaxUpdates.set(subscription, (subscriptionTaxUpdates.get(subscription) ?? 0) + 1);
    }
    return found && Object.assign(found, { metadata: { ...found.metadata, ...fields(form, 'metadata') } });
  }, {}, { 'automatic_tax[enabled]': (value) => value === null || value === 'true' }, ['metadata', 'automatic_tax', 'pending_invoice_item_interval']],
  ['DELETE', /^\/v1\/subscriptions\/(sub_\w+)$/, (form, [key]) => {
    const subscription = find('sub', key);
    if (subscription?.status === 'canceled') return { refused: 'A canceled subscription can only update its metadata and cancellation_details.' };
    if (!subscription) return undefined;
    log.cancels.push({ subscription: subscription.id, customer: subscription.customer, organization: subscription.metadata.organization_id });
    return Object.assign(subscription, { status: 'canceled' });
  }],
  ['GET', /^\/v1\/invoices$/, (query) => list(owned('in', query.get('customer')).filter((invoice) => (!query.has('subscription') || invoice.parent.subscription_details.subscription === query.get('subscription')) && (!query.has('status') || invoice.status === query.get('status')))), { customer: customerId }, {}, ['customer', 'subscription', 'status', 'limit', 'starting_after']],
  ['GET', /^\/v1\/invoice_payments$/, (query) => list([...objects.values()].filter((payment) => payment.id.startsWith('inpay_') && payment.invoice === query.get('invoice') && payment.status === query.get('status'))), { invoice: /^in_\w+$/, status: /^paid$/ }, {}, ['invoice', 'status', 'limit']],
  ['GET', /^\/v1\/refunds$/, (query) => list([...objects.values()].filter((refund) => refund.id.startsWith('re_') && refund.payment_intent === query.get('payment_intent'))), { payment_intent: /^pi_\w+$/ }, {}, ['payment_intent', 'charge', 'limit']],
  ['POST', /^\/v1\/refunds$/, (form) => {
    const payment = find('pi', form.get('payment_intent'));
    if (!payment || Number(form.get('amount')) !== payment.amount) return undefined;
    const refund = store({ id: id('re'), amount: payment.amount, payment_intent: payment.id, status: refundStatuses.get(payment.id) ?? 'succeeded' });
    log.refunds.push({ payment: payment.id, subscription: payment.subscription, amount: refund.amount });
    return refund;
  }, { payment_intent: /^pi_\w+$/ }, {}, ['payment_intent', 'charge', 'amount', 'reason']],
  ['POST', /^\/v1\/invoiceitems$/, (form) => find('sub', form.get('subscription'))?.status === 'canceled' ? { refused: 'Invoice items cannot be added to a canceled subscription.' } : store({ id: id('ii'), object: 'invoiceitem', date: billingClock ?? now(), customer: form.get('customer'), currency: form.get('currency'), quantity: 1, amount: Number(form.get('amount')), description: form.get('description'), metadata: fields(form, 'metadata'), parent: { type: 'subscription_details', subscription_details: { subscription: form.get('subscription') } } }), { customer: customerId }, {}, ['customer', 'subscription', 'currency', 'amount', 'description', 'metadata']],
  ['GET', /^\/v1\/invoiceitems$/, (query) => paged(owned('ii', query.get('customer')).filter((item) => inRange(query, item.date)), query, (item) => item.date), { customer: customerId }, {}, ['customer', 'created', 'limit', 'starting_after']],
];

function complete(session, start = now()) {
  const { price, metadata } = checkouts.get(session.id);
  const renewal = new Date(start * 1000);
  renewal.setUTCMonth(renewal.getUTCMonth() + (price.recurring.interval === 'year' ? 12 : 1));
  const end = Math.floor(renewal.getTime() / 1000);
  const method = store({ id: id('pm'), card: { brand: 'visa', exp_month: 12, exp_year: 2034, last4: '4242' }, customer: session.customer, type: 'card' });
  const invoice = id('in');
  const subscriptionId = id('sub');
  const payment = store({ id: id('pi'), amount: price.unit_amount, subscription: subscriptionId });
  store({ id: id('inpay'), invoice, status: 'paid', amount_paid: price.unit_amount, payment: { type: 'payment_intent', payment_intent: payment.id } });
  store({
    id: invoice, amount_paid: price.unit_amount, parent: { type: 'subscription_details', subscription_details: { subscription: subscriptionId } }, amount_due: price.unit_amount, amount_remaining: 0, created: start, currency: 'usd', customer: session.customer,
    hosted_invoice_url: `https://invoice.stripe.com/i/${invoice}`, invoice_pdf: `https://pay.stripe.com/invoice/${invoice}/pdf`,
    lines: list([{ period: { end, start } }]), number: `${randomBytes(4).toString('hex').toUpperCase()}-0001`, period_end: start, period_start: start, status: 'paid',
  });
  const subscription = store({
    object: 'subscription', id: subscriptionId, customer: session.customer, start_date: start, ended_at: null, canceled_at: null, default_payment_method: method.id, latest_invoice: invoice, metadata, status: 'active', automatic_tax: structuredClone(session.automatic_tax),
    items: list([{ current_period_end: end, current_period_start: start, price: structuredClone(price), quantity: 1 }]),
  });
  record('customer.subscription.created', start, subscription);
  objects.get(session.customer).tax = { automatic_tax: 'supported' };
  objects.get(session.customer).address = { city: 'Sao Paulo', country: 'BR', line1: 'Avenida Paulista 1000', line2: null, postal_code: '01310-100', state: 'SP' };
  return Object.assign(session, { status: 'complete', payment_status: 'paid', subscription: subscription.id, url: null });
}

async function deliver(type, object, created = now()) {
  const payload = JSON.stringify({ id: id('evt'), api_version: apiVersion, data: { object }, livemode: false, type, created });
  const timestamp = now();
  const signature = createHmac('sha256', secret('whsec_')).update(`${timestamp}.${payload}`).digest('hex');
  const answer = await fetch(endpoint, { method: 'POST', headers: { 'content-type': 'application/json', 'stripe-signature': `t=${timestamp},v1=${signature}` }, body: payload });
  deliveredEvents.set(object.id, payload);
  log.events.push({ type, object: object.id, status: answer.status });
}

function page(session) {
  const { price } = checkouts.get(session.id);
  const product = objects.get(price.product);
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Checkout · Stripe</title><link rel="icon" href="data:,"></head><body><main><h1>Subscribe to ${product.name}</h1><p>US$${(price.unit_amount / 100).toFixed(2)} per ${price.recurring.interval}, Visa ending in 4242</p><form method="post"><button type="submit">Subscribe</button></form><a href="${checkouts.get(session.id).cancel}">Cancel</a></main></body></html>`;
}

function reply(response, status, body, headers = {}) {
  const text = typeof body === 'string' ? body : JSON.stringify(body);
  const type = typeof body === 'string' ? 'text/html; charset=utf-8' : 'application/json';
  response.writeHead(status, { 'content-type': type, 'content-length': Buffer.byteLength(text), 'request-id': id('req'), ...headers });
  response.end(text);
}

function unexpected(response, request, url, reason, status = 404, error = { type: 'invalid_request_error', message: `Unrecognized request URL (${request.method}: ${url.pathname}).` }) {
  const line = { method: request.method, host: url.hostname, path: url.pathname, query: keys(url.searchParams), reason };
  log.unexpected.push(line);
  console.log(JSON.stringify({ unexpected: line }));
  reply(response, status, { error });
}

async function handle(request, response, body) {
  const url = new URL(request.url, `https://${request.headers.host}`);
  if (url.hostname === 'auth.terrascale.tech' && request.method === 'POST' && url.pathname === '/v1/terrascale/organizations') {
    const result = await fetch('http://127.0.0.1:8090' + url.pathname, {
      method: 'POST', headers: { authorization: request.headers.authorization, 'content-type': 'application/json' }, body,
    });
    const answer = await result.json();
    if (result.ok && authFailures.delete(JSON.parse(body).idempotency_key)) {
      return reply(response, 503, { error: 'Journey dropped the response after organization creation.' });
    }
    return reply(response, result.status, answer);
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-clock') {
    billingClock = Math.floor(new Date(JSON.parse(body).now).getTime() / 1000);
    return reply(response, 200, { now: new Date(JSON.parse(body).now).toISOString() });
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-subscription') {
    const { organization, price: selected = "price_starter_month", start, customer: existing } = JSON.parse(body);
    const price = selected === "unmapped" ? store({ ...find("price", "price_starter_month"), id: id("price"), unit_amount: 1234 }) : find("price", selected);
    const customer = existing ? find('cus', existing) : store({ id: id('cus'), metadata: { organization_id: organization } });
    const session = store({ id: id('cs_test'), customer: customer.id, automatic_tax: { enabled: true } });
    checkouts.set(session.id, { price, metadata: { organization_id: organization, terrascale_tax: 'automatic' } });
    complete(session, Math.floor(new Date(start).getTime() / 1000));
    const subscription = find('sub', session.subscription);
    return reply(response, 200, { customer: customer.id, subscription: subscription.id });
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-plan-change') {
    const { subscription: key, price, at } = JSON.parse(body);
    const subscription = find('sub', key);
    if (!subscription || !find('price', price)) return reply(response, 404, {});
    subscription.items.data[0].price = structuredClone(find('price', price));
    record('customer.subscription.updated', Math.floor(new Date(at).getTime() / 1000), subscription);
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-cancel') {
    const { subscription: key, at } = JSON.parse(body);
    const subscription = find('sub', key);
    if (!subscription) return reply(response, 404, {});
    const ended = Math.floor(new Date(at).getTime() / 1000);
    Object.assign(subscription, { status: 'canceled', ended_at: ended, canceled_at: ended });
    record('customer.subscription.deleted', ended, subscription);
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-item') {
    const { customer, subscription, amount, quantity = 1, unitAmountDecimal = null, metadata } = JSON.parse(body);
    const item = store({ id: id('ii'), object: 'invoiceitem', date: billingClock ?? now(), customer, currency: 'usd', quantity, unit_amount_decimal: unitAmountDecimal, amount, description: 'Journey provider item', metadata, parent: { type: 'subscription_details', subscription_details: { subscription } } });
    return reply(response, 200, { id: item.id });
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-hold') {
    const { method, path, customer } = JSON.parse(body);
    holds.set(`${method} ${path}`, { customer, arrived: deferred(), released: deferred(), taken: false });
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-held') {
    const { method, path } = JSON.parse(body);
    await holds.get(`${method} ${path}`)?.arrived.promise;
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-release') {
    const { method, path } = JSON.parse(body);
    holds.get(`${method} ${path}`)?.released.resolve();
    holds.delete(`${method} ${path}`);
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'GET' && url.pathname === '/billing-items') {
    const items = owned('ii', url.searchParams.get('customer')).filter((item) => item.metadata.terrascale_month === url.searchParams.get('month'));
    return reply(response, 200, { count: items.length, amount: items.reduce((sum, item) => sum + item.amount, 0) });
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/billing-forget-idempotency') {
    replays.clear();
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/replay-event') {
    const payload = deliveredEvents.get(JSON.parse(body).object);
    if (!payload) return reply(response, 404, {});
    const timestamp = now();
    const signature = createHmac('sha256', secret('whsec_')).update(`${timestamp}.${payload}`).digest('hex');
    const answer = await fetch(endpoint, { method: 'POST', headers: { 'content-type': 'application/json', 'stripe-signature': `t=${timestamp},v1=${signature}` }, body: payload });
    return reply(response, 200, { status: answer.status });
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/auth-failure') {
    authFailures.add(JSON.parse(body).checkout);
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'GET' && url.pathname === '/subscription-tax-state') {
    return reply(response, 200, { updates: subscriptionTaxUpdates.get(url.searchParams.get('subscription')) ?? 0, enabled: find('sub', url.searchParams.get('subscription'))?.automatic_tax?.enabled ?? false });
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/subscription-tax') {
    const { subscription: key, location = 'supported', policy } = JSON.parse(body);
    const subscription = find('sub', key);
    if (!subscription) return reply(response, 404, {});
    objects.get(subscription.customer).tax = { automatic_tax: location };
    if (policy === 'existing') delete subscription.metadata.terrascale_tax;
    if (policy === 'new') subscription.metadata.terrascale_tax = 'automatic';
    subscription.automatic_tax = { enabled: false };
    await deliver('customer.subscription.updated', structuredClone(subscription));
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/payment') {
    const { subscription: key, paid, status, invoiceStatus, created = now(), deliver: notify = true, eventType } = JSON.parse(body);
    const subscription = find('sub', key);
    if (!subscription) return reply(response, 404, {});
    const invoice = owned('in', subscription.customer).find((item) => item.parent.subscription_details.subscription === key);
    subscription.status = status ?? (paid ? 'active' : 'past_due');
    invoice.status = invoiceStatus ?? (paid ? 'paid' : 'open');
    invoice.object = 'invoice';
    invoice.amount_remaining = paid ? 0 : invoice.amount_due;
    if (notify) await deliver(eventType ?? (invoiceStatus === 'void' ? 'invoice.voided' : invoiceStatus === 'uncollectible' ? 'invoice.marked_uncollectible' : paid ? 'invoice.paid' : 'invoice.payment_failed'), structuredClone(invoice), created);
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'GET' && url.pathname === '/log') return reply(response, 200, log);
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/failure') {
    const failure = JSON.parse(body);
    failures.set(failure.method + ' ' + failure.path, failure);
    return reply(response, 200, {});
  }
  if (url.hostname === '127.0.0.1' && request.method === 'POST' && url.pathname === '/refund-status') {
    const { payment, status } = JSON.parse(body);
    refundStatuses.set(payment, status);
    for (const refund of objects.values()) {
      if (refund.id.startsWith('re_') && refund.payment_intent === payment) refund.status = status;
    }
    return reply(response, 200, {});
  }
  if (url.hostname === 'checkout.stripe.com') {
    const session = find('cs', /^\/c\/pay\/(cs_test_\w+)$/.exec(url.pathname)?.[1]);
    if (session?.status !== 'open' || !['GET', 'POST'].includes(request.method)) return unexpected(response, request, url, 'no open checkout session');
    log.calls.push({ method: request.method, path: 'checkout.stripe.com/c/pay/{cs_test}', query: keys(url.searchParams), status: request.method === 'GET' ? 200 : 303 });
    if (request.method === 'GET') return reply(response, 200, page(session));
    await deliver('checkout.session.completed', structuredClone(complete(session)));
    return reply(response, 303, '', { location: checkouts.get(session.id).success.replace('{CHECKOUT_SESSION_ID}', session.id) });
  }
  if (url.hostname !== 'api.stripe.com') return unexpected(response, request, url, 'host');
  if (request.headers.authorization !== `Bearer ${secret('sk_test_')}`) return unexpected(response, request, url, 'API key');
  if (request.headers['stripe-version'] !== apiVersion) return unexpected(response, request, url, `Stripe-Version ${request.headers['stripe-version']}`);
  const parameters = request.method === 'POST' ? new URLSearchParams(body) : url.searchParams;
  const route = api.find(([method, path]) => method === request.method && path.test(url.pathname));
  const unfiltered = Object.entries(route?.[3] ?? {}).find(([key, value]) => !value.test(parameters.get(key) ?? ''))?.[0];
  if (unfiltered) return unexpected(response, request, url, `filter ${unfiltered}`);
  const failure = failures.get(request.method + ' ' + url.pathname);
  if (failure && (failure.remaining === null || failure.remaining > 0)) {
    if (failure.remaining !== null) failure.remaining -= 1;
    log.calls.push({ method: request.method, path: url.pathname, status: failure.status });
    if (failure.transport) { response.destroy(); return; }
    return reply(response, failure.status, { error: { type: 'invalid_request_error', code: failure.status === 404 ? 'resource_missing' : undefined, message: 'Journey Stripe failure.' } });
  }
  const replay = request.method === 'POST' && request.headers['idempotency-key'] ? `${request.method} ${url.pathname} ${request.headers['idempotency-key']}` : '';
  const sent = [...parameters.entries()].sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0).map(([key, value]) => `${key}=${value}`).join('&');
  const stored = replay ? replays.get(replay) : undefined;
  if (stored && stored.parameters !== sent) {
    return reply(response, 400, { error: { type: 'idempotency_error', message: 'Keys for idempotent requests can only be used with the same parameters they were first used with.' } });
  }
  if (stored) return reply(response, stored.status, stored.body);
  const modeled = route?.[5];
  const unknown = modeled && [...parameters.keys()].find((key) => !modeled.some((prefix) => key === prefix || key.startsWith(`${prefix}[`)));
  if (unknown) return unexpected(response, request, url, `parameter ${unknown}`, 400, { type: 'invalid_request_error', param: unknown, message: `Received unknown parameter: ${unknown}` });
  const invalid = Object.entries(route?.[4] ?? {}).find(([key, valid]) => !valid(parameters.get(key)))?.[0];
  if (invalid) return unexpected(response, request, url, `parameter ${invalid}`, 400, { type: 'invalid_request_error', param: invalid, message: `Invalid ${invalid}.` });
  const found = route?.[2](parameters, route[1].exec(url.pathname).slice(1));
  const hold = holds.get(`${request.method} ${url.pathname}`);
  if (found && hold && !hold.taken && parameters.get('customer') === hold.customer) {
    hold.taken = true;
    hold.arrived.resolve();
    await hold.released.promise;
  }
  if (!found) return unexpected(response, request, url, route ? 'no such object' : 'route');
  if (found.refused) return reply(response, 400, { error: { type: 'invalid_request_error', message: found.refused } });
  const answer = render(found, parameters);
  if (replay) replays.set(replay, { parameters: sent, status: 200, body: answer });
  const expanded = [...parameters].filter(([key]) => key.startsWith('expand[')).map(([, path]) => path);
  log.calls.push({ method: request.method, path: url.pathname.replace(/\/(cus|cs_test|sub)_\w+/g, (_, kind) => `/{${kind}}`), query: keys(url.searchParams), expand: expanded, status: 200 });
  return reply(response, 200, answer);
}

createServer((request, response) => {
  const chunks = [];
  request.on('data', (chunk) => chunks.push(chunk));
  request.on('end', () =>
    handle(request, response, Buffer.concat(chunks).toString()).catch((error) => {
      log.unexpected.push({ method: request.method, host: request.headers.host, path: request.url.split('?')[0], reason: String(error) });
      reply(response, 500, { error: { type: 'api_error', message: 'The Stripe stand-in failed on this request.' } });
    }),
  );
}).listen(8027, '127.0.0.1');
