import { u as head } from "../../../chunks/index-server.js";
import { t as NotFound } from "../../../chunks/NotFound.js";
//#region src/routes/404/+page.svelte
function _page($$renderer) {
	head("15h4f4n", $$renderer, ($$renderer) => {
		$$renderer.title(($$renderer) => {
			$$renderer.push(`<title>404 | TerraScale</title>`);
		});
	});
	NotFound($$renderer, {});
}
//#endregion
export { _page as default };
