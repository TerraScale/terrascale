import { $ as attr, i as attr_class, l as ensure_array_like, tt as escape_html, u as head } from "../../../chunks/index-server.js";
import { t as ExternalLink } from "../../../chunks/ExternalLink.js";
import { t as Icon } from "../../../chunks/Icon.js";
import { t as RouteLink } from "../../../chunks/RouteLink.js";
import { t as PricingSection } from "../../../chunks/PricingSection.js";
//#region src/components/sections/PricingFeatureTriad.svelte
function PricingFeatureTriad($$renderer) {
	const items = [
		{
			icon: "shieldCheck",
			title: "Launch Region Controls",
			text: "Create databases in sfo, fra, or gru"
		},
		{
			icon: "zap",
			title: "Observable By Default",
			text: "OpenTelemetry traces and logs across core workflows"
		},
		{
			icon: "trendingUp",
			title: "Usage Guardrails",
			text: "Metered plans with platform-level billing controls"
		}
	];
	$$renderer.push(`<section class="triad svelte-d12h1l"><div class="triad__inner svelte-d12h1l"><div class="triad__grid svelte-d12h1l"><!--[-->`);
	const each_array = ensure_array_like(items);
	for (let $$index = 0, $$length = each_array.length; $$index < $$length; $$index++) {
		let it = each_array[$$index];
		$$renderer.push(`<div class="triad__card ts-glass svelte-d12h1l"><div class="triad__icon svelte-d12h1l">`);
		Icon($$renderer, {
			name: it.icon,
			className: "triad__icon-svg"
		});
		$$renderer.push(`<!----></div> <div><div class="triad__title svelte-d12h1l">${escape_html(it.title)}</div> <div class="triad__text svelte-d12h1l">${escape_html(it.text)}</div></div></div>`);
	}
	$$renderer.push(`<!--]--></div></div></section>`);
}
//#endregion
//#region src/components/sections/DetailedPricingTable.svelte
function DetailedPricingTable($$renderer) {
	const rows = [
		{
			item: "Enterprise Level Support",
			price: "$499",
			unit: "/month",
			details: "Base platform access for Enterprise deployments and advanced support."
		},
		{
			item: "Storage Usage",
			price: "$1",
			unit: "/GB",
			details: "Usage-based storage pricing on top of the base platform fee."
		},
		{
			item: "Database Operations",
			price: "$1",
			unit: "/ 1M ops",
			details: "Scales with workload volume for read, write, and replication activity."
		}
	];
	$$renderer.push(`<section id="detailed-pricing" class="detailed svelte-11sx21j"><div class="detailed__inner svelte-11sx21j"><div class="detailed__eyebrow svelte-11sx21j">Detailed Pricing</div> <h2 class="detailed__title svelte-11sx21j">Clear pricing for custom deployments</h2> <p class="detailed__text svelte-11sx21j">Usage-based costs, support scopes, and deployment requirements are broken out below so
      enterprise teams can estimate spend before talking to sales.</p> `);
	ExternalLink($$renderer, {
		href: "https://docs.terrascale.tech/pricing",
		target: "_blank",
		rel: "noopener noreferrer",
		class: "detailed__docs",
		children: ($$renderer) => {
			Icon($$renderer, {
				name: "bookOpen",
				className: "detailed__docs-icon"
			});
			$$renderer.push(`<!----> Full Pricing Docs `);
			Icon($$renderer, {
				name: "externalLink",
				className: "detailed__docs-icon"
			});
			$$renderer.push(`<!---->`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----> <div class="detailed__table ts-glass svelte-11sx21j"><div class="detailed__head svelte-11sx21j"><div>Item</div> <div>Pricing</div> <div>Details</div></div> <!--[-->`);
	const each_array = ensure_array_like(rows);
	for (let i = 0, $$length = each_array.length; i < $$length; i++) {
		let row = each_array[i];
		$$renderer.push(`<div${attr_class("detailed__row svelte-11sx21j", void 0, { "is-last": i === rows.length - 1 })}><div class="detailed__item svelte-11sx21j">${escape_html(row.item)}</div> <div class="detailed__price svelte-11sx21j"><span class="detailed__price-value ts-text-gradient svelte-11sx21j">${escape_html(row.price)}</span> <span class="detailed__price-unit svelte-11sx21j">${escape_html(row.unit)}</span></div> <div class="detailed__details svelte-11sx21j">${escape_html(row.details)}</div></div>`);
	}
	$$renderer.push(`<!--]--></div></div></section>`);
}
//#endregion
//#region src/components/sections/PricingFAQ.svelte
function PricingFAQ($$renderer) {
	const faqs = [
		{
			q: "What is a Database Operation?",
			a: "A Database Operation is any single read, write, update, or delete action against your database. Each query you execute — whether fetching a row, inserting a record, running an aggregate, or replicating to another region — counts as one or more operations depending on complexity."
		},
		{
			q: "What if I need more operations or storage?",
			a: `All plans support overage billing at usage rates ($1 per additional GB of storage, $1 per million additional operations). You'll never be cut off mid-request — we'll notify you well before you approach limits so you can upgrade or adjust.`
		},
		{
			q: "When can I change my subscription?",
			a: "Anytime. Upgrades take effect immediately with prorated billing; downgrades apply at the start of your next billing cycle. No lock-in, no cancellation fees."
		},
		{
			q: "Can I export my data and leave anytime?",
			a: `Yes — your data is yours. Full exports are available anytime through the dashboard or API, and we never charge a cent to get it out. The only constraint is a per-request rate limit so a runaway export job can't destabilize the cluster for other tenants; there's no cap on total volume and no exit fee. If TerraScale isn't working for you, you leave with everything you put in.`
		},
		{
			q: "Do you offer custom enterprise solutions?",
			a: "Yes. For teams with specific compliance requirements, custom SLAs, or dedicated infrastructure, our Enterprise plan is fully tailored. Contact sales to scope a deployment that fits your needs."
		},
		{
			q: "Why do you require a credit card for the free trial?",
			a: `We require a valid card to prevent abuse and ensure serious usage of our infrastructure. You won't be charged during the 30-day trial, and you can cancel anytime. This also makes the transition to a paid plan seamless if you choose to continue.`
		}
	];
	let open = 0;
	$$renderer.push(`<section class="faq svelte-odh6or"><div class="faq__inner svelte-odh6or"><div class="faq__head svelte-odh6or"><h2 class="faq__title svelte-odh6or">Frequently Asked Questions</h2> <p class="faq__subtitle svelte-odh6or">Get answers to common questions about our pricing and plans.</p></div> <div class="faq__list svelte-odh6or"><!--[-->`);
	const each_array = ensure_array_like(faqs);
	for (let i = 0, $$length = each_array.length; i < $$length; i++) {
		let faq = each_array[i];
		$$renderer.push(`<div${attr_class("faq__item svelte-odh6or", void 0, { "is-open": open === i })}><button type="button" class="faq__button svelte-odh6or"${attr("aria-expanded", open === i)}${attr("aria-controls", `faq-panel-${i}`)}><span>${escape_html(faq.q)}</span> <span${attr_class("faq__chev svelte-odh6or", void 0, { "is-open": open === i })}>`);
		Icon($$renderer, {
			name: "chevronDown",
			className: "faq__chev-icon"
		});
		$$renderer.push(`<!----></span></button> <div${attr("id", `faq-panel-${i}`)}${attr_class("faq__panel svelte-odh6or", void 0, { "is-open": open === i })}><div class="faq__panel-inner svelte-odh6or">${escape_html(faq.a)}</div></div></div>`);
	}
	$$renderer.push(`<!--]--></div> <div class="faq__footer svelte-odh6or"><h3 class="faq__footer-title svelte-odh6or">Still have questions?</h3> <p class="faq__footer-text svelte-odh6or">Our team is here to help you find the perfect plan for your needs.</p> <div class="faq__footer-actions svelte-odh6or">`);
	RouteLink($$renderer, {
		href: "/contact",
		class: "ts-btn ts-btn-primary ts-btn-compact cta-shine",
		children: ($$renderer) => {
			Icon($$renderer, {
				name: "messageSquare",
				className: "faq__footer-icon"
			});
			$$renderer.push(`<!----> Contact Sales`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----> `);
	ExternalLink($$renderer, {
		href: "https://docs.terrascale.tech",
		target: "_blank",
		rel: "noopener noreferrer",
		class: "ts-btn ts-btn-glass ts-btn-compact",
		children: ($$renderer) => {
			Icon($$renderer, {
				name: "bookOpen",
				className: "faq__footer-icon"
			});
			$$renderer.push(`<!----> Read Documentation`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----></div></div></div></section>`);
}
//#endregion
//#region src/components/sections/PricingCTA.svelte
function PricingCTA($$renderer) {
	$$renderer.push(`<section class="pricing-cta svelte-1fxtpb1"><div class="pricing-cta__inner svelte-1fxtpb1"><h2 class="pricing-cta__title svelte-1fxtpb1">Ready to get started?</h2> <p class="pricing-cta__text svelte-1fxtpb1">Join thousands of developers building the future with TerraScale.</p> `);
	RouteLink($$renderer, {
		href: "/contact",
		class: "ts-btn ts-btn-primary cta-shine",
		children: ($$renderer) => {
			$$renderer.push(`<!---->Start Building Today `);
			Icon($$renderer, {
				name: "arrowRight",
				className: "pricing-cta__icon"
			});
			$$renderer.push(`<!---->`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----></div></section>`);
}
//#endregion
//#region src/pages/Pricing.svelte
function Pricing($$renderer) {
	PricingSection($$renderer, {});
	$$renderer.push(`<!----> `);
	PricingFeatureTriad($$renderer, {});
	$$renderer.push(`<!----> `);
	DetailedPricingTable($$renderer, {});
	$$renderer.push(`<!----> `);
	PricingFAQ($$renderer, {});
	$$renderer.push(`<!----> `);
	PricingCTA($$renderer, {});
	$$renderer.push(`<!---->`);
}
//#endregion
//#region src/routes/pricing/+page.svelte
function _page($$renderer) {
	head("1hrotn9", $$renderer, ($$renderer) => {
		$$renderer.title(($$renderer) => {
			$$renderer.push(`<title>TerraScale Pricing</title>`);
		});
		$$renderer.push(`<meta name="description" content="Explore TerraScale pricing plans for hobby projects, growing teams, and launch-region database workflows."/>`);
	});
	Pricing($$renderer, {});
}
//#endregion
export { _page as default };
