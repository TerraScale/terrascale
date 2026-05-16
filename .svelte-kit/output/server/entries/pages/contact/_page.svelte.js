import { $ as attr, i as attr_class, l as ensure_array_like, tt as escape_html, u as head } from "../../../chunks/index-server.js";
import { t as ExternalLink } from "../../../chunks/ExternalLink.js";
import { t as Icon } from "../../../chunks/Icon.js";
//#region src/pages/Contact.svelte
function Contact($$renderer) {
	const channels = [
		{
			icon: "mail",
			label: "Email",
			value: "support@terrascale.tech",
			href: "mailto:support@terrascale.tech",
			kind: "mailto"
		},
		{
			icon: "brandDiscord",
			label: "Discord",
			value: "Join our community",
			href: "https://discord.gg/terrascale",
			kind: "external"
		},
		{
			icon: "brandGithub",
			label: "GitHub",
			value: "Open issues & PRs",
			href: "https://git.mariogk.com/Terrascale",
			kind: "external"
		}
	];
	let form = {
		name: "",
		email: "",
		company: "",
		message: ""
	};
	let submitted = false;
	$$renderer.push(`<section class="contact-hero svelte-1ffqsuu"><div class="contact-hero__inner svelte-1ffqsuu"><div class="contact-hero__eyebrow svelte-1ffqsuu">`);
	Icon($$renderer, {
		name: "messageSquare",
		className: "contact-hero__eyebrow-icon"
	});
	$$renderer.push(`<!----> Get in Touch</div> <h1 class="contact-hero__title svelte-1ffqsuu">Let's build<br/> <span class="ts-text-gradient">something together.</span></h1> <p class="contact-hero__subtitle svelte-1ffqsuu">Have questions? Need a custom plan? Want to integrate with TerraScale? We'd love to hear from
      you.</p></div></section> <section class="contact-body svelte-1ffqsuu"><div class="contact-body__inner svelte-1ffqsuu"><div class="contact-body__grid svelte-1ffqsuu"><form class="contact-form ts-glass svelte-1ffqsuu"><h2 class="contact-form__title svelte-1ffqsuu">Send us a message</h2> <div class="contact-form__row svelte-1ffqsuu"><div><label class="ts-input-label" for="contact-name">Name</label> <input id="contact-name" class="ts-input" type="text" autocomplete="name" placeholder="Mario Belchior"${attr("value", form.name)} required=""/></div> <div><label class="ts-input-label" for="contact-email">Email</label> <input id="contact-email" class="ts-input" type="email" autocomplete="email" placeholder="mario@acme.com"${attr("value", form.email)} required=""/></div></div> <div class="contact-form__field svelte-1ffqsuu"><label class="ts-input-label" for="contact-company">Company</label> <input id="contact-company" class="ts-input" type="text" autocomplete="organization" placeholder="Acme Inc."${attr("value", form.company)}/></div> <div class="contact-form__field svelte-1ffqsuu"><label class="ts-input-label" for="contact-message">Message</label> <textarea id="contact-message" class="ts-input ts-input--textarea" rows="5" placeholder="Tell us about your project, team size, and what you're hoping to achieve..." required="">`);
	const $$body = escape_html(form.message);
	if ($$body) $$renderer.push(`${$$body}`);
	$$renderer.push(`</textarea></div> <button type="submit"${attr_class("contact-form__submit svelte-1ffqsuu", void 0, { "is-submitted": submitted })}>`);
	$$renderer.push("<!--[-1-->");
	$$renderer.push(`Send Message `);
	Icon($$renderer, {
		name: "arrowRight",
		className: "contact-form__submit-icon"
	});
	$$renderer.push(`<!---->`);
	$$renderer.push(`<!--]--></button> <div class="contact-form__note svelte-1ffqsuu">`);
	Icon($$renderer, {
		name: "shield",
		className: "contact-form__note-icon"
	});
	$$renderer.push(`<!----> Your information is never shared. Replies within 24h.</div></form> <div class="contact-channels"><h2 class="contact-channels__title svelte-1ffqsuu">Other ways to reach us</h2> <div class="contact-channels__list svelte-1ffqsuu"><!--[-->`);
	const each_array = ensure_array_like(channels);
	for (let $$index = 0, $$length = each_array.length; $$index < $$length; $$index++) {
		let c = each_array[$$index];
		if (c.kind === "external") {
			$$renderer.push("<!--[0-->");
			ExternalLink($$renderer, {
				href: c.href,
				target: "_blank",
				rel: "noopener noreferrer",
				class: "contact-channel ts-glass",
				children: ($$renderer) => {
					$$renderer.push(`<div class="contact-channel__icon svelte-1ffqsuu">`);
					Icon($$renderer, {
						name: c.icon,
						className: "contact-channel__icon-svg"
					});
					$$renderer.push(`<!----></div> <div class="contact-channel__body svelte-1ffqsuu"><div class="contact-channel__label svelte-1ffqsuu">${escape_html(c.label)}</div> <div class="contact-channel__value svelte-1ffqsuu">${escape_html(c.value)}</div></div> `);
					Icon($$renderer, {
						name: "chevronRight",
						className: "contact-channel__chev"
					});
					$$renderer.push(`<!---->`);
				},
				$$slots: { default: true }
			});
		} else {
			$$renderer.push("<!--[-1-->");
			ExternalLink($$renderer, {
				href: c.href,
				class: "contact-channel ts-glass",
				children: ($$renderer) => {
					$$renderer.push(`<div class="contact-channel__icon svelte-1ffqsuu">`);
					Icon($$renderer, {
						name: c.icon,
						className: "contact-channel__icon-svg"
					});
					$$renderer.push(`<!----></div> <div class="contact-channel__body svelte-1ffqsuu"><div class="contact-channel__label svelte-1ffqsuu">${escape_html(c.label)}</div> <div class="contact-channel__value svelte-1ffqsuu">${escape_html(c.value)}</div></div> `);
					Icon($$renderer, {
						name: "chevronRight",
						className: "contact-channel__chev"
					});
					$$renderer.push(`<!---->`);
				},
				$$slots: { default: true }
			});
		}
		$$renderer.push(`<!--]-->`);
	}
	$$renderer.push(`<!--]--></div> <div class="contact-sla svelte-1ffqsuu"><div class="contact-sla__heading svelte-1ffqsuu"><span class="pulse-dot contact-sla__dot svelte-1ffqsuu"></span> Support SLA</div> <div class="contact-sla__body svelte-1ffqsuu">Pro customers get <strong>priority routing</strong> with 1-hour first response during
            business hours.</div></div></div></div></div></section>`);
}
//#endregion
//#region src/routes/contact/+page.svelte
function _page($$renderer) {
	head("1bv7ezn", $$renderer, ($$renderer) => {
		$$renderer.title(($$renderer) => {
			$$renderer.push(`<title>Contact TerraScale</title>`);
		});
		$$renderer.push(`<meta name="description" content="Get in touch with TerraScale for support, sales questions, or guidance on scaling your database infrastructure."/>`);
	});
	Contact($$renderer, {});
}
//#endregion
export { _page as default };
