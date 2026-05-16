import "./index-server.js";
import { t as Icon } from "./Icon.js";
import { t as RouteLink } from "./RouteLink.js";
//#region src/pages/NotFound.svelte
function NotFound($$renderer) {
	$$renderer.push(`<section class="notfound svelte-kbj945"><div class="notfound__glow svelte-kbj945" aria-hidden="true"></div> <div class="notfound__grid svelte-kbj945" aria-hidden="true"></div> <div class="notfound__inner svelte-kbj945"><div class="notfound__eyebrow svelte-kbj945">`);
	Icon($$renderer, {
		name: "alertCircle",
		className: "notfound__eyebrow-icon"
	});
	$$renderer.push(`<!----> Page Not Found</div> <div class="notfound__code ts-text-gradient svelte-kbj945">404</div> <h1 class="notfound__title svelte-kbj945">We can't find that page.</h1> <p class="notfound__text svelte-kbj945">The page you're looking for has moved, or never existed. Let's get you back on track.</p> <div class="notfound__actions svelte-kbj945">`);
	RouteLink($$renderer, {
		href: "/",
		class: "ts-btn ts-btn-primary cta-shine",
		children: ($$renderer) => {
			Icon($$renderer, {
				name: "home",
				className: "notfound__icon"
			});
			$$renderer.push(`<!----> Back to Home`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----> `);
	RouteLink($$renderer, {
		href: "/contact",
		class: "ts-btn ts-btn-glass",
		children: ($$renderer) => {
			Icon($$renderer, {
				name: "messageSquare",
				className: "notfound__icon"
			});
			$$renderer.push(`<!----> Contact Support`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----></div> <div class="notfound__links svelte-kbj945"><span class="notfound__links-label svelte-kbj945">Popular pages:</span> `);
	RouteLink($$renderer, {
		href: "/",
		class: "notfound__link",
		children: ($$renderer) => {
			$$renderer.push(`<!---->Home`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----> `);
	RouteLink($$renderer, {
		href: "/pricing",
		class: "notfound__link",
		children: ($$renderer) => {
			$$renderer.push(`<!---->Pricing`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----> `);
	RouteLink($$renderer, {
		href: "/contact",
		class: "notfound__link",
		children: ($$renderer) => {
			$$renderer.push(`<!---->Contact`);
		},
		$$slots: { default: true }
	});
	$$renderer.push(`<!----></div></div></section>`);
}
//#endregion
export { NotFound as t };
