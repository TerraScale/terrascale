import { createHmac, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';

const apiVersion = '2026-09-30.endive';
const endpoint = 'https://dash.terrascale.tech/api/v1/billing/stripe/webhook';
const objects = new Map();
const checkouts = new Map();
const replays = new Map();
const log = { calls: [], events: [], cancels: [], unexpected: [] };

const now = () => Math.floor(Date.now() / 1000);
const hour = 3600;
const within = (earliest, latest) => (value) => value === null || (/^\d+$/.test(value) && Number(value) >= now() + earliest && Number(value) <= now() + latest);
const id = (prefix) => `${prefix}_${randomBytes(12).toString('hex')}`;
const list = (data) => ({ data, has_more: false });
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
  ['GET', /^\/v1\/prices$/, () => list([...objects.values()].filter((object) => object.id.startsWith('price_')))],
  ['POST', /^\/v1\/customers$/, (form) => store({ id: id('cus'), metadata: fields(form, 'metadata'), name: form.get('name') })],
  ['GET', /^\/v1\/customers\/search$/, (query) => {
    const organization = /^metadata\['organization_id'\]:'([0-9A-Z]{26})'$/.exec(query.get('query') ?? '')?.[1];
    if (!organization) return undefined;
    const customers = [...objects.values()].filter((object) => object.id.startsWith('cus_') && object.metadata.organization_id === organization);
    return { data: customers, has_more: false, next_page: null };
  }],
  ['GET', /^\/v1\/customers\/(cus_\w+)$/, (query, [customer]) => find('cus', customer)],
  ['POST', /^\/v1\/checkout\/sessions$/, (form) => {
    const price = find('price', form.get('line_items[0][price]'));
    if (form.get('mode') !== 'subscription' || !price || form.get('line_items[0][quantity]') !== '1' || !find('cus', form.get('customer'))) return undefined;
    const session = store({
      object: 'checkout.session', id: id('cs_test'), client_reference_id: form.get('client_reference_id'), customer: form.get('customer'),
      metadata: fields(form, 'metadata'), mode: 'subscription', status: 'open', url: null,
    });
    session.url = `https://checkout.stripe.com/c/pay/${session.id}`;
    checkouts.set(session.id, { price, metadata: fields(form, 'subscription_data[metadata]'), success: form.get('success_url') });
    return session;
  }, {}, { expires_at: within(hour / 2, 24 * hour) }],
  ['GET', /^\/v1\/checkout\/sessions$/, (query) => list(owned('cs', query.get('customer'))), { customer: customerId }],
  ['GET', /^\/v1\/checkout\/sessions\/(cs_test_\w+)$/, (query, [session]) => find('cs', session)],
  ['GET', /^\/v1\/subscriptions$/, (query) => list(owned('sub', query.get('customer'))), { customer: customerId, status: /^all$/ }],
  ['GET', /^\/v1\/subscriptions\/(sub_\w+)$/, (query, [subscription]) => find('sub', subscription)],
  ['DELETE', /^\/v1\/subscriptions\/(sub_\w+)$/, (form, [key]) => {
    const subscription = find('sub', key);
    if (subscription?.status === 'canceled') return { refused: 'canceled subscription', message: 'A canceled subscription can only update its metadata and cancellation_details.' };
    if (!subscription) return undefined;
    log.cancels.push({ subscription: subscription.id, customer: subscription.customer, organization: subscription.metadata.organization_id });
    return Object.assign(subscription, { status: 'canceled' });
  }],
  ['GET', /^\/v1\/invoices$/, (query) => list(owned('in', query.get('customer'))), { customer: customerId }],
  ['GET', /^\/v1\/invoiceitems$/, () => list([]), { customer: customerId }],
];

function complete(session) {
  const { price, metadata } = checkouts.get(session.id);
  const start = now();
  const renewal = new Date(start * 1000);
  renewal.setUTCMonth(renewal.getUTCMonth() + (price.recurring.interval === 'year' ? 12 : 1));
  const end = Math.floor(renewal.getTime() / 1000);
  const method = store({ id: id('pm'), card: { brand: 'visa', exp_month: 12, exp_year: 2034, last4: '4242' }, customer: session.customer, type: 'card' });
  const invoice = id('in');
  store({
    id: invoice, amount_due: price.unit_amount, amount_remaining: 0, created: start, currency: 'usd', customer: session.customer,
    hosted_invoice_url: `https://invoice.stripe.com/i/${invoice}`, invoice_pdf: `https://pay.stripe.com/invoice/${invoice}/pdf`,
    lines: list([{ period: { end, start } }]), number: `${randomBytes(4).toString('hex').toUpperCase()}-0001`, period_end: start, period_start: start, status: 'paid',
  });
  const subscription = store({
    id: id('sub'), customer: session.customer, default_payment_method: method.id, metadata, status: 'active',
    items: list([{ current_period_end: end, current_period_start: start, price: structuredClone(price), quantity: 1 }]),
  });
  objects.get(session.customer).address = { city: 'Sao Paulo', country: 'BR', line1: 'Avenida Paulista 1000', line2: null, postal_code: '01310-100', state: 'SP' };
  return Object.assign(session, { status: 'complete', subscription: subscription.id, url: null });
}

async function deliver(type, object) {
  const payload = JSON.stringify({ id: id('evt'), api_version: apiVersion, data: { object }, livemode: false, type });
  const timestamp = now();
  const signature = createHmac('sha256', secret('whsec_')).update(`${timestamp}.${payload}`).digest('hex');
  const answer = await fetch(endpoint, { method: 'POST', headers: { 'content-type': 'application/json', 'stripe-signature': `t=${timestamp},v1=${signature}` }, body: payload });
  log.events.push({ type, object: object.id, status: answer.status });
}

function page(session) {
  const { price } = checkouts.get(session.id);
  const product = objects.get(price.product);
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Checkout · Stripe</title><link rel="icon" href="data:,"></head><body><main><h1>Subscribe to ${product.name}</h1><p>US$${(price.unit_amount / 100).toFixed(2)} per ${price.recurring.interval}, Visa ending in 4242</p><form method="post"><button type="submit">Subscribe</button></form></main></body></html>`;
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
  if (url.hostname === '127.0.0.1' && request.method === 'GET' && url.pathname === '/log') return reply(response, 200, log);
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
  const replay = request.method === 'POST' && request.headers['idempotency-key'] ? `${request.method} ${url.pathname} ${request.headers['idempotency-key']}` : '';
  if (replays.has(replay)) return reply(response, 200, replays.get(replay));
  const invalid = Object.entries(route?.[4] ?? {}).find(([key, valid]) => !valid(parameters.get(key)))?.[0];
  if (invalid) return unexpected(response, request, url, `parameter ${invalid}`, 400, { type: 'invalid_request_error', param: invalid, message: `Invalid ${invalid}.` });
  const found = route?.[2](parameters, route[1].exec(url.pathname).slice(1));
  if (!found) return unexpected(response, request, url, route ? 'no such object' : 'route');
  if (found.refused) return unexpected(response, request, url, found.refused, 400, { type: 'invalid_request_error', message: found.message });
  const answer = render(found, parameters);
  if (replay) replays.set(replay, answer);
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
