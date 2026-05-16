import { $ as attr, et as clsx, i as attr_class, lt as fallback, s as bind_props, v as html } from "./index-server.js";
//#endregion
//#region src/components/Icon.svelte
function Icon($$renderer, $$props) {
	$$renderer.component(($$renderer) => {
		let name = $$props["name"];
		let className = fallback($$props["className"], "");
		let label = fallback($$props["label"], void 0);
		$$renderer.push(`<span${attr_class(clsx(`ts-icon ${className}`.trim()), "svelte-13so817")}${attr("role", label ? "img" : void 0)}${attr("aria-label", label)}${attr("aria-hidden", label ? void 0 : true)}>${html("")}</span>`);
		bind_props($$props, {
			name,
			className,
			label
		});
	});
}
//#endregion
export { Icon as t };
