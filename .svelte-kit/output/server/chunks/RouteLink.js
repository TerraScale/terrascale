import { _ as unsubscribe_stores, et as clsx, f as rest_props, h as store_get, lt as fallback, m as slot, o as attributes, p as sanitize_props, pt as getContext, s as bind_props, t as index_server_exports, ut as noop } from "./index-server.js";
import { y as noop$1 } from "./shared.js";
import { d as base, f as initial_base } from "./environment.js";
import { s as resolve_route } from "./exports.js";
import "./internal.js";
import "@sveltejs/kit/internal";
import { try_get_request_store } from "@sveltejs/kit/internal/server";
//#region node_modules/@sveltejs/kit/src/runtime/app/paths/server.js
/** @type {import('./client.js').resolve} */
function resolve(id, params) {
	const resolved = resolve_route(id, params);
	{
		const store = try_get_request_store();
		if (store && !store.state.prerendering?.fallback) return (store.event.url.pathname.slice(initial_base.length).split("/").slice(2).map(() => "..").join("/") || ".") + resolved;
	}
	return base + resolved;
}
var PRELOAD_PRIORITIES = {
	tap: 1,
	hover: 2,
	viewport: 3,
	eager: 4,
	off: -1,
	false: -1
};
({ ...PRELOAD_PRIORITIES }), PRELOAD_PRIORITIES.hover;
var updated_listener = { v: noop$1 };
var updated;
var is_legacy = noop.toString().includes("$$") || /function \w+\(\) \{\}/.test(noop.toString());
var placeholder_url = "a:";
if (is_legacy) {
	new URL(placeholder_url);
	updated = { current: false };
} else {
	new class Page {
		data = {};
		form = null;
		error = null;
		params = {};
		route = { id: null };
		state = {};
		status = -1;
		url = new URL(placeholder_url);
	}();
	new class Navigating {
		current = null;
	}();
	updated = new class Updated {
		current = false;
	}();
	updated_listener.v = () => updated.current = true;
}
//#endregion
//#region node_modules/@sveltejs/kit/src/runtime/client/client.js
/** @import { RemoteQueryCacheEntry } from './remote-functions/query.svelte.js' */
var { onMount, tick } = index_server_exports;
//#endregion
//#region node_modules/@sveltejs/kit/src/runtime/app/stores.js
/**
* A function that returns all of the contextual stores. On the server, this must be called during component initialization.
* Only use this if you need to defer store subscription until after the component has mounted, for some reason.
*
* @deprecated Use `$app/state` instead (requires Svelte 5, [see docs for more info](https://svelte.dev/docs/kit/migrating-to-sveltekit-2#SvelteKit-2.12:-$app-stores-deprecated))
*/
var getStores = () => {
	const stores$1 = getContext("__svelte__");
	return {
		page: { subscribe: stores$1.page.subscribe },
		navigating: { subscribe: stores$1.navigating.subscribe },
		updated: stores$1.updated
	};
};
/**
* A readable store whose value contains page data.
*
* On the server, this store can only be subscribed to during component initialization. In the browser, it can be subscribed to at any time.
*
* @deprecated Use `page` from `$app/state` instead (requires Svelte 5, [see docs for more info](https://svelte.dev/docs/kit/migrating-to-sveltekit-2#SvelteKit-2.12:-$app-stores-deprecated))
* @type {import('svelte/store').Readable<import('@sveltejs/kit').Page>}
*/
var page = { subscribe(fn) {
	return getStores().page.subscribe(fn);
} };
//#endregion
//#region src/components/RouteLink.svelte
function RouteLink($$renderer, $$props) {
	const $$restProps = rest_props(sanitize_props($$props), [
		"class",
		"href",
		"activeClass",
		"inactiveClass",
		"match",
		"afterClick"
	]);
	$$renderer.component(($$renderer) => {
		var $$store_subs;
		let internal, resolvedHref, targetPath, normalizedTargetPath, isActive;
		let className = fallback($$props["class"], "");
		let href = fallback($$props["href"], "");
		let activeClass = fallback($$props["activeClass"], "");
		let inactiveClass = fallback($$props["inactiveClass"], "");
		let match = fallback($$props["match"], "exact");
		let afterClick = fallback($$props["afterClick"], void 0);
		const isInternalHref = (value) => value.startsWith("/") || value.startsWith("#");
		const toResolvedHref = (value) => {
			if (value.startsWith("#")) return value;
			const [pathname, hash = ""] = value.split("#");
			const resolvedPath = resolve(pathname || "/");
			return hash ? `${resolvedPath}#${hash}` : resolvedPath;
		};
		$: internal = isInternalHref(href);
		$: resolvedHref = internal ? toResolvedHref(href) : href;
		$: targetPath = href.startsWith("#") ? store_get($$store_subs ??= {}, "$page", page).url.pathname : href.split("#")[0] || "/";
		$: normalizedTargetPath = targetPath.length > 1 && targetPath.endsWith("/") ? targetPath.slice(0, -1) : targetPath;
		$: isActive = internal && (match === "prefix" ? normalizedTargetPath === "/" ? store_get($$store_subs ??= {}, "$page", page).url.pathname === "/" : store_get($$store_subs ??= {}, "$page", page).url.pathname.startsWith(normalizedTargetPath) : store_get($$store_subs ??= {}, "$page", page).url.pathname === normalizedTargetPath);
		$$renderer.push(`<a${attributes({
			href: resolvedHref,
			class: clsx(`${className} ${isActive ? activeClass : inactiveClass}`.trim()),
			...$$restProps
		})}><!--[-->`);
		slot($$renderer, $$props, "default", {}, null);
		$$renderer.push(`<!--]--></a>`);
		if ($$store_subs) unsubscribe_stores($$store_subs);
		bind_props($$props, {
			class: className,
			href,
			activeClass,
			inactiveClass,
			match,
			afterClick
		});
	});
}
//#endregion
export { page as n, RouteLink as t };
