import { $ as attr, _ as unsubscribe_stores, a as attr_style, h as store_get, i as attr_class, l as ensure_array_like, lt as fallback, s as bind_props, tt as escape_html, ut as noop, z as writable } from "./index-server.js";
import "./internal.js";
import { t as Icon } from "./Icon.js";
import { t as RouteLink } from "./RouteLink.js";
//#region src/components/sections/SectionHeader.svelte
function SectionHeader($$renderer, $$props) {
	let eyebrow = $$props["eyebrow"];
	let title = $$props["title"];
	let subtitle = fallback($$props["subtitle"], "");
	$$renderer.push(`<div class="section-header svelte-1jilx4n"><div class="section-header__eyebrow svelte-1jilx4n"><span class="section-header__eyebrow-dot svelte-1jilx4n" aria-hidden="true"></span> ${escape_html(eyebrow)}</div> <h2 class="section-header__title svelte-1jilx4n">${escape_html(title)}</h2> `);
	if (subtitle) {
		$$renderer.push("<!--[0-->");
		$$renderer.push(`<p class="section-header__subtitle svelte-1jilx4n">${escape_html(subtitle)}</p>`);
	} else $$renderer.push("<!--[-1-->");
	$$renderer.push(`<!--]--></div>`);
	bind_props($$props, {
		eyebrow,
		title,
		subtitle
	});
}
globalThis.Date;
globalThis.Set;
globalThis.Map;
globalThis.URL;
globalThis.URLSearchParams;
//#endregion
//#region node_modules/svelte/src/internal/client/timing.js
/** @import { Raf } from '#client' */
var now = () => Date.now();
/** @type {Raf} */
var raf = {
	tick: (_) => noop(_),
	now: () => now(),
	tasks: /* @__PURE__ */ new Set()
};
//#endregion
//#region node_modules/svelte/src/internal/client/loop.js
/** @import { TaskCallback, Task, TaskEntry } from '#client' */
/**
* @returns {void}
*/
function run_tasks() {
	const now = raf.now();
	raf.tasks.forEach((task) => {
		if (!task.c(now)) {
			raf.tasks.delete(task);
			task.f();
		}
	});
	if (raf.tasks.size !== 0) raf.tick(run_tasks);
}
/**
* Creates a new task that runs on each raf frame
* until it returns a falsy value or is aborted
* @param {TaskCallback} callback
* @returns {Task}
*/
function loop(callback) {
	/** @type {TaskEntry} */
	let task;
	if (raf.tasks.size === 0) raf.tick(run_tasks);
	return {
		promise: new Promise((fulfill) => {
			raf.tasks.add(task = {
				c: callback,
				f: fulfill
			});
		}),
		abort() {
			raf.tasks.delete(task);
		}
	};
}
//#endregion
//#region node_modules/svelte/src/motion/utils.js
/**
* @param {any} obj
* @returns {obj is Date}
*/
function is_date(obj) {
	return Object.prototype.toString.call(obj) === "[object Date]";
}
//#endregion
//#region node_modules/svelte/src/easing/index.js
/**
* @param {number} t
* @returns {number}
*/
function linear(t) {
	return t;
}
/**
* @param {number} t
* @returns {number}
*/
function cubicOut(t) {
	const f = t - 1;
	return f * f * f + 1;
}
if (typeof HTMLElement === "function");
//#endregion
//#region node_modules/svelte/src/motion/tweened.js
/** @import { Task } from '../internal/client/types' */
/** @import { Tweened, TweenOptions } from './public' */
/**
* @template T
* @param {T} a
* @param {T} b
* @returns {(t: number) => T}
*/
function get_interpolator(a, b) {
	if (a === b || a !== a) return () => a;
	const type = typeof a;
	if (type !== typeof b || Array.isArray(a) !== Array.isArray(b)) throw new Error("Cannot interpolate values of different type");
	if (Array.isArray(a)) {
		const arr = b.map((bi, i) => {
			return get_interpolator(
				/** @type {Array<any>} */
				a[i],
				bi
			);
		});
		return (t) => arr.map((fn) => fn(t));
	}
	if (type === "object") {
		if (!a || !b) throw new Error("Object cannot be null");
		if (is_date(a) && is_date(b)) {
			const an = a.getTime();
			const delta = b.getTime() - an;
			return (t) => new Date(an + t * delta);
		}
		const keys = Object.keys(b);
		/** @type {Record<string, (t: number) => T>} */
		const interpolators = {};
		keys.forEach((key) => {
			interpolators[key] = get_interpolator(a[key], b[key]);
		});
		return (t) => {
			/** @type {Record<string, any>} */
			const result = {};
			keys.forEach((key) => {
				result[key] = interpolators[key](t);
			});
			return result;
		};
	}
	if (type === "number") {
		const delta = b - a;
		return (t) => a + t * delta;
	}
	return () => b;
}
/**
* A tweened store in Svelte is a special type of store that provides smooth transitions between state values over time.
*
* @deprecated Use [`Tween`](https://svelte.dev/docs/svelte/svelte-motion#Tween) instead
* @template T
* @param {T} [value]
* @param {TweenOptions<T>} [defaults]
* @returns {Tweened<T>}
*/
function tweened(value, defaults = {}) {
	const store = writable(value);
	/** @type {Task} */
	let task;
	let target_value = value;
	/**
	* @param {T} new_value
	* @param {TweenOptions<T>} [opts]
	*/
	function set(new_value, opts) {
		target_value = new_value;
		if (value == null) {
			store.set(value = new_value);
			return Promise.resolve();
		}
		/** @type {Task | null} */
		let previous_task = task;
		let started = false;
		let { delay = 0, duration = 400, easing = linear, interpolate = get_interpolator } = {
			...defaults,
			...opts
		};
		if (duration === 0) {
			if (previous_task) {
				previous_task.abort();
				previous_task = null;
			}
			store.set(value = target_value);
			return Promise.resolve();
		}
		const start = raf.now() + delay;
		/** @type {(t: number) => T} */
		let fn;
		task = loop((now) => {
			if (now < start) return true;
			if (!started) {
				fn = interpolate(value, new_value);
				if (typeof duration === "function") duration = duration(value, new_value);
				started = true;
			}
			if (previous_task) {
				previous_task.abort();
				previous_task = null;
			}
			const elapsed = now - start;
			if (elapsed > duration) {
				store.set(value = new_value);
				return false;
			}
			store.set(value = fn(easing(elapsed / duration)));
			return true;
		});
		return task.promise;
	}
	return {
		set,
		update: (fn, opts) => set(fn(target_value, value), opts),
		subscribe: store.subscribe
	};
}
//#endregion
//#region src/components/AnimatedPrice.svelte
function AnimatedPrice($$renderer, $$props) {
	$$renderer.component(($$renderer) => {
		var $$store_subs;
		let value = $$props["value"];
		let prefix = fallback($$props["prefix"], "$");
		let duration = fallback($$props["duration"], 650);
		const display = tweened(value, {
			duration,
			easing: cubicOut
		});
		const format = (n) => {
			const rounded = Math.round(n * 100) / 100;
			return Number.isInteger(rounded) ? rounded.toString() : rounded.toFixed(2);
		};
		$: display.set(value);
		$$renderer.push(`<span class="animated-price svelte-l1uyha"><span class="animated-price__prefix svelte-l1uyha">${escape_html(prefix)}</span><span class="animated-price__value">${escape_html(format(store_get($$store_subs ??= {}, "$display", display)))}</span></span>`);
		if ($$store_subs) unsubscribe_stores($$store_subs);
		bind_props($$props, {
			value,
			prefix,
			duration
		});
	});
}
//#endregion
//#region src/components/sections/PricingSection.svelte
function PricingSection($$renderer) {
	let billing = "monthly";
	const plans = [
		{
			name: "Hobby",
			icon: "zap",
			accent: "var(--ts-secondary)",
			accentRgb: "var(--ts-secondary-rgb)",
			price: {
				monthly: 4.99,
				annual: 49
			},
			description: "Great for personal projects and prototypes",
			features: [
				"3 databases",
				"1M operations",
				"10 GB storage",
				"15 GB egress",
				"Priority Discord"
			],
			cta: "Start Free Trial",
			href: "/contact",
			popular: false
		},
		{
			name: "Pro",
			icon: "trendingUp",
			accent: "var(--ts-primary)",
			accentRgb: "var(--ts-primary-rgb)",
			price: {
				monthly: 24.99,
				annual: 249
			},
			description: "Best for growing teams and production",
			features: [
				"15 databases",
				"10M operations",
				"50 GB storage",
				"100 GB egress",
				"Email + Discord",
				"Priority support"
			],
			cta: "Start Free Trial",
			href: "/contact",
			popular: true
		},
		{
			name: "Enterprise",
			icon: "shieldCheck",
			accent: "var(--ts-tertiary)",
			accentRgb: "var(--ts-tertiary-rgb)",
			price: null,
			basePrice: 499,
			description: "For mission-critical applications",
			features: [
				"Unlimited databases",
				"Custom SLAs",
				"Dedicated support",
				"Advanced security",
				"Custom integrations"
			],
			cta: "Contact Sales",
			href: "/contact",
			popular: false
		}
	];
	const formatSuffix = (plan) => plan.price ? billing === "annual" ? "/year" : "/month" : "";
	$$renderer.push(`<section class="pricing-section svelte-af4xsc" id="pricing"><div class="pricing-section__inner svelte-af4xsc">`);
	SectionHeader($$renderer, {
		eyebrow: "Pricing",
		title: "Simple pricing that scales with you",
		subtitle: "Start free, scale on demand. No hidden fees, no surprises."
	});
	$$renderer.push(`<!----> <div class="pricing-toggle-wrap svelte-af4xsc"><div class="pricing-toggle svelte-af4xsc" role="tablist" aria-label="Billing cycle"><div${attr_class("pricing-toggle__thumb svelte-af4xsc", void 0, { "is-annual": billing === "annual" })} aria-hidden="true"></div> <button type="button"${attr_class("pricing-toggle__btn svelte-af4xsc", void 0, { "is-active": billing === "monthly" })} role="tab"${attr("aria-selected", billing === "monthly")}>Monthly</button> <button type="button"${attr_class("pricing-toggle__btn svelte-af4xsc", void 0, { "is-active": billing === "annual" })} role="tab"${attr("aria-selected", billing === "annual")}>Annual</button> <span${attr_class("pricing-toggle__save svelte-af4xsc", void 0, { "is-active": billing === "annual" })}><svg viewBox="0 0 24 24" aria-hidden="true" class="svelte-af4xsc"><path d="M5 12l5 5L20 7" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"></path></svg> Save 20%</span></div></div> <div class="pricing-grid svelte-af4xsc"><!--[-->`);
	const each_array = ensure_array_like(plans);
	for (let $$index_1 = 0, $$length = each_array.length; $$index_1 < $$length; $$index_1++) {
		let plan = each_array[$$index_1];
		$$renderer.push(`<div${attr_class("pricing-card svelte-af4xsc", void 0, { "is-popular": plan.popular })}${attr_style(`--plan-accent: ${plan.accent}; --plan-accent-rgb: ${plan.accentRgb};`)}>`);
		if (plan.popular) {
			$$renderer.push("<!--[0-->");
			$$renderer.push(`<div class="pricing-card__badge svelte-af4xsc">`);
			Icon($$renderer, {
				name: "star",
				className: "pricing-card__badge-icon"
			});
			$$renderer.push(`<!----> Most Popular</div>`);
		} else $$renderer.push("<!--[-1-->");
		$$renderer.push(`<!--]--> <div class="pricing-card__header svelte-af4xsc"><div class="pricing-card__icon svelte-af4xsc">`);
		Icon($$renderer, {
			name: plan.icon,
			className: "pricing-card__icon-svg"
		});
		$$renderer.push(`<!----></div> <div class="pricing-card__name svelte-af4xsc">${escape_html(plan.name)}</div></div> <p class="pricing-card__desc svelte-af4xsc">${escape_html(plan.description)}</p> <div class="pricing-card__price-row svelte-af4xsc">`);
		if (plan.price) {
			$$renderer.push("<!--[0-->");
			$$renderer.push(`<div class="pricing-card__price svelte-af4xsc">`);
			AnimatedPrice($$renderer, { value: plan.price[billing] });
			$$renderer.push(`<!----></div> <div class="pricing-card__suffix svelte-af4xsc">${escape_html(formatSuffix(plan))}</div>`);
		} else {
			$$renderer.push("<!--[-1-->");
			$$renderer.push(`<div class="pricing-card__price svelte-af4xsc">Custom</div>`);
		}
		$$renderer.push(`<!--]--></div> `);
		if (!plan.price && plan.basePrice) {
			$$renderer.push("<!--[0-->");
			$$renderer.push(`<div class="pricing-card__note svelte-af4xsc">starts at $${escape_html(plan.basePrice)}/mo + usage</div>`);
		} else $$renderer.push("<!--[-1-->");
		$$renderer.push(`<!--]--> <div class="pricing-card__divider svelte-af4xsc"></div> <ul class="pricing-card__features svelte-af4xsc"><!--[-->`);
		const each_array_1 = ensure_array_like(plan.features);
		for (let $$index = 0, $$length = each_array_1.length; $$index < $$length; $$index++) {
			let feature = each_array_1[$$index];
			$$renderer.push(`<li class="svelte-af4xsc"><span class="pricing-card__check svelte-af4xsc">`);
			Icon($$renderer, {
				name: "check",
				className: "pricing-card__check-icon"
			});
			$$renderer.push(`<!----></span> <span>${escape_html(feature)}</span></li>`);
		}
		$$renderer.push(`<!--]--></ul> `);
		RouteLink($$renderer, {
			href: plan.href,
			class: "pricing-card__cta",
			children: ($$renderer) => {
				$$renderer.push(`<!---->${escape_html(plan.cta)} `);
				Icon($$renderer, {
					name: "arrowRight",
					className: "pricing-card__cta-icon"
				});
				$$renderer.push(`<!---->`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----></div>`);
	}
	$$renderer.push(`<!--]--></div></div></section>`);
}
//#endregion
export { SectionHeader as n, PricingSection as t };
