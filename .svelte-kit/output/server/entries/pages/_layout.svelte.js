import { $ as attr, _ as unsubscribe_stores, h as store_get, i as attr_class, l as ensure_array_like, m as slot, tt as escape_html, u as head } from "../../chunks/index-server.js";
import { t as ExternalLink } from "../../chunks/ExternalLink.js";
import { t as Icon } from "../../chunks/Icon.js";
import { n as page, t as RouteLink } from "../../chunks/RouteLink.js";
//#region src/lib/site.ts
var navigation = [
	{
		name: "Home",
		href: "/",
		icon: "home"
	},
	{
		name: "Pricing",
		href: "/pricing",
		icon: "dollarSign"
	},
	{
		name: "Contact",
		href: "/contact",
		icon: "mail"
	}
];
var docsLink = {
	name: "Docs",
	href: "https://docs.terrascale.tech",
	icon: "bookOpen"
};
var footerColumns = [
	{
		title: "Product",
		links: [
			{
				name: "Features",
				href: "/#features"
			},
			{
				name: "Pricing",
				href: "/pricing"
			},
			{
				name: "Changelog",
				href: "https://docs.terrascale.tech/changelog"
			},
			{
				name: "Roadmap",
				href: "https://docs.terrascale.tech/roadmap"
			},
			{
				name: "Documentation",
				href: "https://docs.terrascale.tech"
			}
		]
	},
	{
		title: "Company",
		links: [{
			name: "Blog",
			href: "https://blog.terrascale.tech"
		}, {
			name: "Contact",
			href: "/contact"
		}]
	},
	{
		title: "Resources",
		links: [
			{
				name: "Docs",
				href: "https://docs.terrascale.tech"
			},
			{
				name: "API Reference",
				href: "https://docs.terrascale.tech/api"
			},
			{
				name: "Status",
				href: "https://status.terrascale.tech"
			},
			{
				name: "Community",
				href: "https://discord.gg/terrascale"
			},
			{
				name: "Support",
				href: "/contact"
			}
		]
	},
	{
		title: "Legal",
		links: [
			{
				name: "Privacy",
				href: "https://docs.terrascale.tech/privacy"
			},
			{
				name: "Terms",
				href: "https://docs.terrascale.tech/terms"
			},
			{
				name: "Security",
				href: "https://docs.terrascale.tech/security"
			},
			{
				name: "SLA",
				href: "https://docs.terrascale.tech/sla"
			},
			{
				name: "Compliance",
				href: "https://docs.terrascale.tech/compliance"
			}
		]
	}
];
var socialLinks = [
	{
		name: "GitHub",
		href: "https://git.mariogk.com/Terrascale",
		icon: "brandGithub",
		color: "#F1F5F9",
		bg: "rgba(241,245,249,0.08)",
		border: "rgba(241,245,249,0.18)"
	},
	{
		name: "Discord",
		href: "https://discord.gg/terrascale",
		icon: "brandDiscord",
		color: "#5865F2",
		bg: "rgba(88,101,242,0.12)",
		border: "rgba(88,101,242,0.35)"
	},
	{
		name: "X",
		href: "https://twitter.com/terrascale",
		icon: "brandX",
		color: "#FFFFFF",
		bg: "rgba(255,255,255,0.06)",
		border: "rgba(255,255,255,0.18)"
	},
	{
		name: "LinkedIn",
		href: "https://linkedin.com/company/terrascale",
		icon: "brandLinkedin",
		color: "#0A66C2",
		bg: "rgba(10,102,194,0.14)",
		border: "rgba(10,102,194,0.38)"
	},
	{
		name: "Email",
		href: "mailto:support@terrascale.tech",
		icon: "mail",
		color: "#10B981",
		bg: "rgba(16,185,129,0.12)",
		border: "rgba(16,185,129,0.35)"
	}
];
//#endregion
//#region src/components/Footer.svelte
function Footer($$renderer, $$props) {
	$$renderer.component(($$renderer) => {
		const year = (/* @__PURE__ */ new Date()).getFullYear();
		const isExternal = (href) => href.startsWith("http") || href.startsWith("mailto:");
		$$renderer.push(`<footer class="ts-footer svelte-1sr6y3t"><div class="ts-footer__inner svelte-1sr6y3t"><div class="ts-footer__grid svelte-1sr6y3t"><div class="ts-footer__brand svelte-1sr6y3t">`);
		RouteLink($$renderer, {
			href: "/",
			class: "ts-footer__logo",
			"aria-label": "TerraScale home",
			children: ($$renderer) => {
				$$renderer.push(`<img src="/logo-white.svg" alt="TerraScale" width="1429" height="345" decoding="async" loading="lazy"/>`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----> <p class="ts-footer__tagline svelte-1sr6y3t">Empowering teams with secure, scalable database infrastructure. Scale your tech, effortlessly.</p> <div class="ts-footer__socials svelte-1sr6y3t"><!--[-->`);
		const each_array = ensure_array_like(socialLinks);
		for (let $$index = 0, $$length = each_array.length; $$index < $$length; $$index++) {
			let social = each_array[$$index];
			ExternalLink($$renderer, {
				href: social.href,
				target: "_blank",
				rel: "noopener noreferrer",
				"aria-label": social.name,
				class: "ts-footer__social",
				style: `color: ${social.color}; background: ${social.bg}; border-color: ${social.border};`,
				children: ($$renderer) => {
					Icon($$renderer, {
						name: social.icon,
						className: "ts-footer__social-icon"
					});
				},
				$$slots: { default: true }
			});
		}
		$$renderer.push(`<!--]--></div></div> <!--[-->`);
		const each_array_1 = ensure_array_like(footerColumns);
		for (let $$index_2 = 0, $$length = each_array_1.length; $$index_2 < $$length; $$index_2++) {
			let column = each_array_1[$$index_2];
			$$renderer.push(`<div class="ts-footer__column"><div class="ts-footer__heading svelte-1sr6y3t">${escape_html(column.title)}</div> <ul class="ts-footer__list svelte-1sr6y3t"><!--[-->`);
			const each_array_2 = ensure_array_like(column.links);
			for (let $$index_1 = 0, $$length = each_array_2.length; $$index_1 < $$length; $$index_1++) {
				let link = each_array_2[$$index_1];
				$$renderer.push(`<li class="svelte-1sr6y3t">`);
				if (isExternal(link.href)) {
					$$renderer.push("<!--[0-->");
					ExternalLink($$renderer, {
						href: link.href,
						target: link.href.startsWith("mailto:") ? void 0 : "_blank",
						rel: link.href.startsWith("mailto:") ? void 0 : "noopener noreferrer",
						class: "ts-footer__link",
						children: ($$renderer) => {
							Icon($$renderer, {
								name: "chevronRight",
								className: "ts-footer__chevron"
							});
							$$renderer.push(`<!----> ${escape_html(link.name)}`);
						},
						$$slots: { default: true }
					});
				} else {
					$$renderer.push("<!--[-1-->");
					RouteLink($$renderer, {
						href: link.href,
						class: "ts-footer__link",
						children: ($$renderer) => {
							Icon($$renderer, {
								name: "chevronRight",
								className: "ts-footer__chevron"
							});
							$$renderer.push(`<!----> ${escape_html(link.name)}`);
						},
						$$slots: { default: true }
					});
				}
				$$renderer.push(`<!--]--></li>`);
			}
			$$renderer.push(`<!--]--></ul></div>`);
		}
		$$renderer.push(`<!--]--></div> <div class="ts-footer__meta svelte-1sr6y3t"><div class="ts-footer__copy svelte-1sr6y3t">© ${escape_html(year)} TerraScale. All rights reserved.</div> <div class="ts-footer__stats svelte-1sr6y3t"><span class="ts-footer__stat svelte-1sr6y3t"><span class="pulse-dot ts-footer__stat-dot svelte-1sr6y3t"></span> sfo · fra · gru</span> <span class="ts-footer__stat svelte-1sr6y3t">`);
		Icon($$renderer, {
			name: "shield",
			className: "ts-footer__stat-icon"
		});
		$$renderer.push(`<!----> OpenTelemetry-ready</span></div></div></div></footer>`);
	});
}
//#endregion
//#region src/components/Header.svelte
function Header($$renderer, $$props) {
	$$renderer.component(($$renderer) => {
		var $$store_subs;
		let scrolled = false;
		let isMenuOpen = false;
		$: if (store_get($$store_subs ??= {}, "$page", page).url.pathname) isMenuOpen = false;
		$$renderer.push(`<header${attr_class("ts-header svelte-oiwvqb", void 0, { "scrolled": scrolled })}><div class="ts-header__inner svelte-oiwvqb">`);
		RouteLink($$renderer, {
			href: "/",
			class: "ts-header__brand",
			"aria-label": "TerraScale home",
			children: ($$renderer) => {
				$$renderer.push(`<img src="/logo-principal.svg" alt="TerraScale" width="1429" height="345" decoding="async" fetchpriority="high"/>`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----> <nav class="ts-nav svelte-oiwvqb" aria-label="Primary"><!--[-->`);
		const each_array = ensure_array_like(navigation);
		for (let $$index = 0, $$length = each_array.length; $$index < $$length; $$index++) {
			let item = each_array[$$index];
			RouteLink($$renderer, {
				href: item.href,
				match: item.href === "/" ? "exact" : "prefix",
				class: "ts-nav__link",
				activeClass: "is-active",
				inactiveClass: "",
				children: ($$renderer) => {
					Icon($$renderer, {
						name: item.icon,
						className: "ts-nav__icon"
					});
					$$renderer.push(`<!----> ${escape_html(item.name)}`);
				},
				$$slots: { default: true }
			});
		}
		$$renderer.push(`<!--]--></nav> <div class="ts-header__actions svelte-oiwvqb">`);
		ExternalLink($$renderer, {
			href: docsLink.href,
			target: "_blank",
			rel: "noopener noreferrer",
			class: "ts-header__docs",
			children: ($$renderer) => {
				Icon($$renderer, {
					name: docsLink.icon,
					className: "ts-nav__icon"
				});
				$$renderer.push(`<!----> ${escape_html(docsLink.name)}`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----> `);
		ExternalLink($$renderer, {
			href: "https://status.terrascale.tech",
			target: "_blank",
			rel: "noopener noreferrer",
			class: "ts-status-link",
			children: ($$renderer) => {
				$$renderer.push(`<span class="pulse-dot ts-status-link__dot svelte-oiwvqb"></span> Status`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----> `);
		RouteLink($$renderer, {
			href: "/contact",
			class: "ts-cta cta-shine",
			children: ($$renderer) => {
				$$renderer.push(`<!---->Get Started `);
				Icon($$renderer, {
					name: "arrowRight",
					className: "ts-cta__icon"
				});
				$$renderer.push(`<!---->`);
			},
			$$slots: { default: true }
		});
		$$renderer.push(`<!----></div> <button type="button" class="ts-header__menu svelte-oiwvqb"${attr("aria-expanded", isMenuOpen)} aria-controls="mobile-navigation" aria-label="Toggle navigation menu">`);
		Icon($$renderer, {
			name: isMenuOpen ? "x" : "menu",
			className: "ts-header__menu-icon"
		});
		$$renderer.push(`<!----></button></div> `);
		if (isMenuOpen) {
			$$renderer.push("<!--[0-->");
			$$renderer.push(`<div id="mobile-navigation" class="ts-header__mobile svelte-oiwvqb"><nav class="ts-mobile-nav svelte-oiwvqb" aria-label="Mobile primary"><!--[-->`);
			const each_array_1 = ensure_array_like(navigation);
			for (let $$index_1 = 0, $$length = each_array_1.length; $$index_1 < $$length; $$index_1++) {
				let item = each_array_1[$$index_1];
				RouteLink($$renderer, {
					href: item.href,
					match: item.href === "/" ? "exact" : "prefix",
					class: "ts-nav__link ts-nav__link--mobile",
					activeClass: "is-active",
					inactiveClass: "",
					afterClick: () => {
						isMenuOpen = false;
					},
					children: ($$renderer) => {
						Icon($$renderer, {
							name: item.icon,
							className: "ts-nav__icon"
						});
						$$renderer.push(`<!----> ${escape_html(item.name)}`);
					},
					$$slots: { default: true }
				});
			}
			$$renderer.push(`<!--]--> `);
			ExternalLink($$renderer, {
				href: docsLink.href,
				target: "_blank",
				rel: "noopener noreferrer",
				class: "ts-nav__link ts-nav__link--mobile",
				children: ($$renderer) => {
					Icon($$renderer, {
						name: docsLink.icon,
						className: "ts-nav__icon"
					});
					$$renderer.push(`<!----> ${escape_html(docsLink.name)}`);
				},
				$$slots: { default: true }
			});
			$$renderer.push(`<!----> `);
			ExternalLink($$renderer, {
				href: "https://status.terrascale.tech",
				target: "_blank",
				rel: "noopener noreferrer",
				class: "ts-status-link ts-status-link--mobile",
				children: ($$renderer) => {
					$$renderer.push(`<span class="pulse-dot ts-status-link__dot svelte-oiwvqb"></span> Status`);
				},
				$$slots: { default: true }
			});
			$$renderer.push(`<!----> `);
			RouteLink($$renderer, {
				href: "/contact",
				class: "ts-cta cta-shine ts-cta--mobile",
				afterClick: () => {
					isMenuOpen = false;
				},
				children: ($$renderer) => {
					$$renderer.push(`<!---->Get Started `);
					Icon($$renderer, {
						name: "arrowRight",
						className: "ts-cta__icon"
					});
					$$renderer.push(`<!---->`);
				},
				$$slots: { default: true }
			});
			$$renderer.push(`<!----></nav></div>`);
		} else $$renderer.push("<!--[-1-->");
		$$renderer.push(`<!--]--></header>`);
		if ($$store_subs) unsubscribe_stores($$store_subs);
	});
}
//#endregion
//#region src/routes/+layout.svelte
function _layout($$renderer, $$props) {
	head("12qhfyh", $$renderer, ($$renderer) => {
		$$renderer.title(($$renderer) => {
			$$renderer.push(`<title>TerraScale | Developer Infrastructure For Data Workflows</title>`);
		});
		$$renderer.push(`<meta name="description" content="TerraScale is developer-first infrastructure for typed database, auth, queue, and payment workflows across the current launch-region model."/>`);
	});
	$$renderer.push(`<div class="min-h-screen bg-[#050508] text-[#F8FAFC] dark">`);
	Header($$renderer, {});
	$$renderer.push(`<!----> <main><!--[-->`);
	slot($$renderer, $$props, "default", {}, null);
	$$renderer.push(`<!--]--></main> `);
	Footer($$renderer, {});
	$$renderer.push(`<!----></div>`);
}
//#endregion
export { _layout as default };
