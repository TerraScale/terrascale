import { u as head } from "../../chunks/index-server.js";
import { t as NotFound } from "../../chunks/NotFound.js";
//#region src/routes/+error.svelte
function _error($$renderer) {
	head("1j96wlh", $$renderer, ($$renderer) => {
		$$renderer.title(($$renderer) => {
			$$renderer.push(`<title>404 | TerraScale</title>`);
		});
	});
	NotFound($$renderer, {});
}
//#endregion
export { _error as default };
