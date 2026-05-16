import { $ as attr, a as attr_style, et as clsx, g as stringify, i as attr_class, l as ensure_array_like, tt as escape_html, u as head } from "../../chunks/index-server.js";
import { t as ExternalLink } from "../../chunks/ExternalLink.js";
import { t as Icon } from "../../chunks/Icon.js";
import { t as RouteLink } from "../../chunks/RouteLink.js";
import { n as SectionHeader, t as PricingSection } from "../../chunks/PricingSection.js";
//#region src/lib/world-map.generated.ts
var WORLD_MAP_VIEWBOX = {
	x: 0,
	y: 0,
	width: 119,
	height: 60
};
var REGIONS = [
	{
		"id": "san-francisco",
		"city": "San Francisco",
		"lat": 37.77,
		"lng": -122.42,
		"x": 16,
		"y": 21.650635094610966
	},
	{
		"id": "sao-paulo",
		"city": "São Paulo",
		"lat": -23.55,
		"lng": -46.63,
		"x": 42.5,
		"y": 45.033320996790806
	},
	{
		"id": "frankfurt",
		"city": "Frankfurt",
		"lat": 50.11,
		"lng": 8.68,
		"x": 62.5,
		"y": 15.588457268119894
	}
];
//#endregion
//#region src/components/sections/Hero.svelte
function Hero($$renderer, $$props) {
	$$renderer.component(($$renderer) => {
		const highlights = [
			{
				icon: "zap",
				label: "Typed API"
			},
			{
				icon: "globe",
				label: "SFO · FRA · GRU"
			},
			{
				icon: "check",
				label: "SDK-backed"
			}
		];
		const vbW = WORLD_MAP_VIEWBOX.width;
		const vbH = WORLD_MAP_VIEWBOX.height;
		const ringPairs = REGIONS.map((r, i) => [r, REGIONS[(i + 1) % REGIONS.length]]);
		const arcPath = (a, b) => {
			const mx = (a.x + b.x) / 2;
			const len = Math.hypot(b.x - a.x, b.y - a.y);
			const lift = Math.max(2.5, len * .18);
			const cy = Math.min(a.y, b.y) - lift - Math.abs(b.x - a.x) * .04;
			return `M ${a.x} ${a.y} Q ${mx} ${cy} ${b.x} ${b.y}`;
		};
		const connections = ringPairs.map(([from, to], i) => ({
			id: `${from.id}-${to.id}`,
			d: arcPath(from, to),
			delay: (i * .45).toFixed(2)
		}));
		$$renderer.push(`<section class="hero svelte-hae3jt"><div class="hero__globe svelte-hae3jt" aria-hidden="true"><div class="hero__globe-frame svelte-hae3jt"><img src="/world-map.svg" alt="" class="hero__globe-map svelte-hae3jt" decoding="async" loading="eager"/> <svg class="hero__globe-lines svelte-hae3jt"${attr("viewBox", `0 0 ${stringify(vbW)} ${stringify(vbH)}`)} preserveAspectRatio="none" aria-hidden="true"><!--[-->`);
		const each_array = ensure_array_like(connections);
		for (let $$index = 0, $$length = each_array.length; $$index < $$length; $$index++) {
			let c = each_array[$$index];
			$$renderer.push(`<path${attr("d", c.d)} class="hero__line svelte-hae3jt"${attr_style(`--delay: ${stringify(c.delay)}s;`)}></path>`);
		}
		$$renderer.push(`<!--]--></svg> <!--[-->`);
		const each_array_1 = ensure_array_like(REGIONS);
		for (let i = 0, $$length = each_array_1.length; i < $$length; i++) {
			let r = each_array_1[i];
			$$renderer.push(`<span class="hero__pin svelte-hae3jt"${attr_style(`left: ${stringify(r.x / vbW * 100)}%; top: ${stringify(r.y / vbH * 100)}%; --delay: ${stringify((i * .3).toFixed(2))}s;`)}${attr("title", r.city)}><span class="hero__pin-ring svelte-hae3jt"></span> <span class="hero__pin-core svelte-hae3jt"></span></span>`);
		}
		$$renderer.push(`<!--]--></div></div> <div class="hero__inner svelte-hae3jt"><div class="hero__brand svelte-hae3jt"><span class="hero__brand-glow svelte-hae3jt" aria-hidden="true"></span> <img src="/logo-principal.svg" alt="TerraScale" class="hero__brand-mark svelte-hae3jt" width="1429" height="345" decoding="async" fetchpriority="high"/></div> <h1 class="hero__headline svelte-hae3jt">Scale your data,<br class="svelte-hae3jt"/> <span class="ts-text-gradient svelte-hae3jt">effortlessly.</span></h1> <p class="hero__subtitle svelte-hae3jt">Typed database, auth, queue, and payment infrastructure for teams building on the
      TerraScale API.</p> <div class="hero__ctas svelte-hae3jt">`);
		RouteLink($$renderer, {
			href: "/contact",
			class: "ts-btn ts-btn-primary cta-shine",
			children: ($$renderer) => {
				$$renderer.push(`<!---->Start Free `);
				Icon($$renderer, {
					name: "arrowRight",
					className: "hero__cta-icon"
				});
				$$renderer.push(`<!---->`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----> `);
		ExternalLink($$renderer, {
			href: "https://docs.terrascale.tech",
			target: "_blank",
			rel: "noopener noreferrer",
			class: "ts-btn ts-btn-glass",
			children: ($$renderer) => {
				$$renderer.push(`<!---->View Docs`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----></div> <ul class="hero__highlights svelte-hae3jt" aria-label="Key capabilities"><!--[-->`);
		const each_array_2 = ensure_array_like(highlights);
		for (let $$index_2 = 0, $$length = each_array_2.length; $$index_2 < $$length; $$index_2++) {
			let h = each_array_2[$$index_2];
			$$renderer.push(`<li class="hero__highlight svelte-hae3jt">`);
			Icon($$renderer, {
				name: h.icon,
				className: "hero__highlight-icon"
			});
			$$renderer.push(`<!----> <span class="svelte-hae3jt">${escape_html(h.label)}</span></li>`);
		}
		$$renderer.push(`<!--]--></ul></div></section>`);
	});
}
//#endregion
//#region src/components/sections/Features.svelte
function Features($$renderer) {
	const features = [
		{
			icon: "zap",
			title: "Typed Data API",
			text: "Create databases, collections, schemas, entities, and SQL queries through one public API.",
			bullets: [
				"Schema plans",
				"SQL explain",
				"SDK queries"
			]
		},
		{
			icon: "shield",
			title: "Auth And Access",
			text: "Account, session, passkey, MFA, organization, invitation, and scoped API-key flows share the same platform surface.",
			bullets: [
				"Passkeys",
				"MFA",
				"Scoped keys"
			]
		},
		{
			icon: "globe",
			title: "Launch Regions",
			text: "Use the product launch regions San Francisco, Frankfurt, and Guarulhos through dashboard and SDK flows.",
			bullets: [
				"sfo",
				"fra",
				"gru"
			]
		},
		{
			icon: "trendingUp",
			title: "Queue And Payments",
			text: "Queue workflows run on JetStream, and payment provider flows sit behind TerraScale Payments.",
			bullets: [
				"NATS JetStream",
				"Polar",
				"Stripe"
			]
		}
	];
	$$renderer.push(`<section class="features svelte-jrak3q" id="features"><div class="features__inner svelte-jrak3q">`);
	SectionHeader($$renderer, {
		eyebrow: "Platform",
		title: "Built for scale, designed for developers",
		subtitle: "The implemented surface spans data, auth, queues, payments, SDKs, and dashboard workflows."
	});
	$$renderer.push(`<!----> <div class="features__grid svelte-jrak3q"><!--[-->`);
	const each_array = ensure_array_like(features);
	for (let $$index_1 = 0, $$length = each_array.length; $$index_1 < $$length; $$index_1++) {
		let feature = each_array[$$index_1];
		$$renderer.push(`<article class="feature-card ts-glass svelte-jrak3q"><div class="feature-card__icon svelte-jrak3q">`);
		Icon($$renderer, {
			name: feature.icon,
			className: "feature-card__icon-svg"
		});
		$$renderer.push(`<!----></div> <h3 class="feature-card__title svelte-jrak3q">${escape_html(feature.title)}</h3> <p class="feature-card__text svelte-jrak3q">${escape_html(feature.text)}</p> <div class="feature-card__bullets svelte-jrak3q"><!--[-->`);
		const each_array_1 = ensure_array_like(feature.bullets);
		for (let $$index = 0, $$length = each_array_1.length; $$index < $$length; $$index++) {
			let bullet = each_array_1[$$index];
			$$renderer.push(`<span class="feature-card__bullet svelte-jrak3q"><span class="feature-card__bullet-dot svelte-jrak3q"></span> ${escape_html(bullet)}</span>`);
		}
		$$renderer.push(`<!--]--></div></article>`);
	}
	$$renderer.push(`<!--]--></div></div></section>`);
}
//#endregion
//#region src/lib/highlight.ts
var KEYWORDS = {
	typescript: /\b(import|from|const|let|var|async|await|function|return|if|else|new|class|extends|export|default|interface|type|as|in|of)\b/g,
	csharp: /\b(using|namespace|class|struct|interface|enum|public|private|protected|internal|static|readonly|const|var|new|return|if|else|for|foreach|in|while|switch|case|default|break|continue|async|await|null|true|false|this|base|void)\b/g
};
var STRINGS = /(["'`])(?:(?=(\\?))\2.)*?\1/g;
var COMMENTS = /(\/\/[^\n]*|#[^\n]*|\/\*[\s\S]*?\*\/)/g;
var NUMBERS = /\b(\d+(?:\.\d+)?)\b/g;
var FNS = /\b([a-zA-Z_][\w]*)(?=\()/g;
function tokenize(code, lang) {
	const tokens = [];
	const claimed = new Array(code.length).fill(false);
	const mark = (regex, className) => {
		regex.lastIndex = 0;
		let m;
		while ((m = regex.exec(code)) !== null) {
			const s = m.index;
			const e = s + m[0].length;
			let clear = true;
			for (let i = s; i < e; i++) if (claimed[i]) {
				clear = false;
				break;
			}
			if (clear) {
				tokens.push({
					start: s,
					end: e,
					text: m[0],
					className
				});
				for (let i = s; i < e; i++) claimed[i] = true;
			}
		}
	};
	mark(COMMENTS, "tok-comment");
	mark(STRINGS, "tok-string");
	mark(KEYWORDS[lang] || KEYWORDS.typescript, "tok-keyword");
	mark(FNS, "tok-fn");
	mark(NUMBERS, "tok-num");
	tokens.sort((a, b) => a.start - b.start);
	const parts = [];
	let i = 0;
	for (const t of tokens) {
		if (t.start > i) parts.push({ text: code.slice(i, t.start) });
		parts.push({
			text: t.text,
			className: t.className
		});
		i = t.end;
	}
	if (i < code.length) parts.push({ text: code.slice(i) });
	return parts;
}
var CODE_SAMPLES = {
	typescript: `import { createTerraScaleClient } from '@terrascale/sdk';

const ts = createTerraScaleClient({
  baseUrl: 'https://api.terrascale.tech',
  apiKey: process.env.TERRASCALE_API_KEY,
});

const database = await ts.databases.create({
  name: 'prod',
  region: 'fra',
});

await ts.schema.apply(database.id, 'customers', {
  fields: [
    { name: 'email', type: 'String', required: true },
    { name: 'plan', type: 'String' },
  ],
  confirmed: true,
});

const rows = await ts.repository<Customer>(database.id, 'customers')
  .query({ filter: { plan: 'pro' }, limit: 50 });`,
	csharp: `using Microsoft.Extensions.Options;
using TerraScale.Client;
using TerraScale.Client.Databases;

using var http = new HttpClient
{
    BaseAddress = new Uri("https://api.terrascale.tech")
};
http.DefaultRequestHeaders.Add(
    "X-TerraScale-Api-Key",
    Environment.GetEnvironmentVariable("TERRASCALE_API_KEY"));

var client = new TerraScaleClient(
    http,
    Options.Create(new TerraScaleClientOptions
    {
        DefaultRegion = TerraScaleRegions.Frankfurt
    }));

var database = await client.Databases.CreateAsync(
    new CreateDatabaseRequest("prod"));

var plans = await client.Payments.ListPlansAsync();`
};
var LANG_META = [{
	id: "csharp",
	label: "C#",
	ext: "cs",
	icon: "/language-icons/csharp.svg"
}, {
	id: "typescript",
	label: "TypeScript",
	ext: "ts",
	icon: "/language-icons/typescript.svg"
}];
//#endregion
//#region src/components/sections/CodePanel.svelte
function CodePanel($$renderer, $$props) {
	$$renderer.component(($$renderer) => {
		let code, tokens, ext;
		let lang = "csharp";
		let copied = false;
		const bullets = [
			"Generated OpenAPI contract catalog",
			"Typed C# and TypeScript clients",
			"Dashboard uses the public SDK boundary",
			"Launch-region inputs for sfo, fra, gru"
		];
		$: code = CODE_SAMPLES[lang];
		$: tokens = tokenize(code, lang);
		$: ext = LANG_META.find((l) => l.id === lang)?.ext ?? "txt";
		$$renderer.push(`<section class="code svelte-12hy3gy"><div class="code__inner svelte-12hy3gy"><div class="code__grid svelte-12hy3gy"><div class="code__copy"><div class="code__eyebrow svelte-12hy3gy">`);
		Icon($$renderer, {
			name: "code2",
			className: "code__eyebrow-icon"
		});
		$$renderer.push(`<!----> Developer SDK</div> <h2 class="code__headline svelte-12hy3gy">Query your data<br/> <span class="ts-text-gradient">in the language you love</span></h2> <p class="code__text svelte-12hy3gy">First-class C# and TypeScript SDKs target the same public HTTP routes used by the
          dashboard, with generated OpenAPI models underneath.</p> <ul class="code__bullets svelte-12hy3gy"><!--[-->`);
		const each_array = ensure_array_like(bullets);
		for (let $$index = 0, $$length = each_array.length; $$index < $$length; $$index++) {
			let bullet = each_array[$$index];
			$$renderer.push(`<li class="code__bullet svelte-12hy3gy"><span class="code__bullet-check svelte-12hy3gy">`);
			Icon($$renderer, {
				name: "check",
				className: "code__bullet-icon"
			});
			$$renderer.push(`<!----></span> <span>${escape_html(bullet)}</span></li>`);
		}
		$$renderer.push(`<!--]--></ul></div> <div class="code__terminal svelte-12hy3gy"><div class="code__chrome svelte-12hy3gy"><div class="code__chrome-left svelte-12hy3gy"><span class="code__dot code__dot--red svelte-12hy3gy"></span> <span class="code__dot code__dot--yellow svelte-12hy3gy"></span> <span class="code__dot code__dot--green svelte-12hy3gy"></span> <span class="code__filename svelte-12hy3gy">~/app/queries.${escape_html(ext)}</span></div> <button type="button"${attr_class("code__copy-btn svelte-12hy3gy", void 0, { "is-copied": copied })} aria-label="Copy code">`);
		Icon($$renderer, {
			name: "code2",
			className: "code__copy-icon"
		});
		$$renderer.push(`<!----> ${escape_html("Copy")}</button></div> <div class="code__tabs svelte-12hy3gy" role="tablist" aria-label="Language examples"><!--[-->`);
		const each_array_1 = ensure_array_like(LANG_META);
		for (let $$index_1 = 0, $$length = each_array_1.length; $$index_1 < $$length; $$index_1++) {
			let l = each_array_1[$$index_1];
			$$renderer.push(`<button type="button"${attr_class("code__tab svelte-12hy3gy", void 0, { "is-active": lang === l.id })} role="tab"${attr("aria-selected", lang === l.id)}><img${attr("src", l.icon)} alt="" width="13" height="13" class="code__tab-icon svelte-12hy3gy"/> ${escape_html(l.label)}</button>`);
		}
		$$renderer.push(`<!--]--></div> <pre class="code__pre svelte-12hy3gy"><code class="svelte-12hy3gy"><!--[-->`);
		const each_array_2 = ensure_array_like(tokens);
		for (let i = 0, $$length = each_array_2.length; i < $$length; i++) {
			let t = each_array_2[i];
			if (t.className) {
				$$renderer.push("<!--[0-->");
				$$renderer.push(`<span${attr_class(clsx(t.className), "svelte-12hy3gy")}>${escape_html(t.text)}</span>`);
			} else {
				$$renderer.push("<!--[-1-->");
				$$renderer.push(`${escape_html(t.text)}`);
			}
			$$renderer.push(`<!--]-->`);
		}
		$$renderer.push(`<!--]--></code></pre></div></div></div></section>`);
	});
}
//#endregion
//#region src/components/sections/CtaSection.svelte
function CtaSection($$renderer) {
	const perks = [
		"Full feature access",
		"Cancel anytime",
		"No lock-in"
	];
	$$renderer.push(`<section class="cta svelte-r7m0cc"><div class="cta__inner svelte-r7m0cc"><div class="cta__card ts-glass svelte-r7m0cc"><h2 class="cta__headline svelte-r7m0cc">Try TerraScale <span class="ts-text-gradient">free for 30 days.</span></h2> <p class="cta__text svelte-r7m0cc">Set up in minutes. Every Hobby and Pro feature, unlocked — nothing billed
        until you decide to keep it.</p> <div class="cta__actions svelte-r7m0cc">`);
	RouteLink($$renderer, {
		href: "/contact",
		class: "ts-btn ts-btn-primary cta-shine cta__primary",
		children: ($$renderer) => {
			$$renderer.push(`<!---->Start 30-Day Free Trial `);
			Icon($$renderer, {
				name: "arrowRight",
				className: "cta__icon"
			});
			$$renderer.push(`<!---->`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----> `);
	RouteLink($$renderer, {
		href: "/contact",
		class: "ts-btn ts-btn-glass cta__secondary",
		children: ($$renderer) => {
			Icon($$renderer, {
				name: "messageSquare",
				className: "cta__icon"
			});
			$$renderer.push(`<!----> Talk to Sales`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----></div> <ul class="cta__perks svelte-r7m0cc" aria-label="Trial details"><!--[-->`);
	const each_array = ensure_array_like(perks);
	for (let $$index = 0, $$length = each_array.length; $$index < $$length; $$index++) {
		let p = each_array[$$index];
		$$renderer.push(`<li class="svelte-r7m0cc">`);
		Icon($$renderer, {
			name: "checkCircle",
			className: "cta__perk-icon"
		});
		$$renderer.push(`<!----> ${escape_html(p)}</li>`);
	}
	$$renderer.push(`<!--]--></ul></div></div></section>`);
}
//#endregion
//#region src/pages/Home.svelte
function Home($$renderer) {
	Hero($$renderer, {});
	$$renderer.push(`<!----> `);
	CodePanel($$renderer, {});
	$$renderer.push(`<!----> `);
	Features($$renderer, {});
	$$renderer.push(`<!----> `);
	PricingSection($$renderer, {});
	$$renderer.push(`<!----> `);
	CtaSection($$renderer, {});
	$$renderer.push(`<!---->`);
}
//#endregion
//#region src/routes/+page.svelte
function _page($$renderer) {
	head("1uha8ag", $$renderer, ($$renderer) => {
		$$renderer.title(($$renderer) => {
			$$renderer.push(`<title>TerraScale | Developer Infrastructure For Data Workflows</title>`);
		});
	});
	Home($$renderer, {});
}
//#endregion
export { _page as default };
