import { et as clsx, f as rest_props, lt as fallback, m as slot, o as attributes, p as sanitize_props, s as bind_props } from "./index-server.js";
//#region src/components/ExternalLink.svelte
function ExternalLink($$renderer, $$props) {
	const $$restProps = rest_props(sanitize_props($$props), [
		"class",
		"href",
		"target",
		"rel"
	]);
	let className = fallback($$props["class"], "");
	let href = fallback($$props["href"], "");
	let target = fallback($$props["target"], void 0);
	let rel = fallback($$props["rel"], void 0);
	$$renderer.push(`<a${attributes({
		href,
		target,
		rel,
		class: clsx(className),
		...$$restProps
	})}><!--[-->`);
	slot($$renderer, $$props, "default", {}, null);
	$$renderer.push(`<!--]--></a>`);
	bind_props($$props, {
		class: className,
		href,
		target,
		rel
	});
}
//#endregion
export { ExternalLink as t };
