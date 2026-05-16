
// this file is generated — do not edit it


declare module "svelte/elements" {
	export interface HTMLAttributes<T> {
		'data-sveltekit-keepfocus'?: true | '' | 'off' | undefined | null;
		'data-sveltekit-noscroll'?: true | '' | 'off' | undefined | null;
		'data-sveltekit-preload-code'?:
			| true
			| ''
			| 'eager'
			| 'viewport'
			| 'hover'
			| 'tap'
			| 'off'
			| undefined
			| null;
		'data-sveltekit-preload-data'?: true | '' | 'hover' | 'tap' | 'off' | undefined | null;
		'data-sveltekit-reload'?: true | '' | 'off' | undefined | null;
		'data-sveltekit-replacestate'?: true | '' | 'off' | undefined | null;
	}
}

export {};


declare module "$app/types" {
	type MatcherParam<M> = M extends (param : string) => param is (infer U extends string) ? U : string;

	export interface AppTypes {
		RouteId(): "/" | "/404" | "/contact" | "/pricing";
		RouteParams(): {
			
		};
		LayoutParams(): {
			"/": Record<string, never>;
			"/404": Record<string, never>;
			"/contact": Record<string, never>;
			"/pricing": Record<string, never>
		};
		Pathname(): "/" | "/404" | "/contact" | "/pricing";
		ResolvedPathname(): `${"" | `/${string}`}${ReturnType<AppTypes['Pathname']>}`;
		Asset(): "/fonts/jetbrains-mono-bold.woff2" | "/fonts/jetbrains-mono.woff2" | "/fonts/quantico-bold.woff2" | "/fonts/quantico.woff2" | "/icon-black.svg" | "/icon-blue.svg" | "/icon-white.svg" | "/icons/lucide/activity.svg" | "/icons/lucide/alert-circle.svg" | "/icons/lucide/arrow-left.svg" | "/icons/lucide/arrow-right.svg" | "/icons/lucide/award.svg" | "/icons/lucide/bar-chart-3.svg" | "/icons/lucide/book-open.svg" | "/icons/lucide/calendar.svg" | "/icons/lucide/check-circle.svg" | "/icons/lucide/check.svg" | "/icons/lucide/chevron-down.svg" | "/icons/lucide/chevron-right.svg" | "/icons/lucide/clock.svg" | "/icons/lucide/code-2.svg" | "/icons/lucide/database.svg" | "/icons/lucide/dollar-sign.svg" | "/icons/lucide/external-link.svg" | "/icons/lucide/globe.svg" | "/icons/lucide/home.svg" | "/icons/lucide/layout-dashboard.svg" | "/icons/lucide/layout-grid.svg" | "/icons/lucide/mail.svg" | "/icons/lucide/menu.svg" | "/icons/lucide/message-circle.svg" | "/icons/lucide/message-square.svg" | "/icons/lucide/search.svg" | "/icons/lucide/send.svg" | "/icons/lucide/shield-check.svg" | "/icons/lucide/shield.svg" | "/icons/lucide/star.svg" | "/icons/lucide/target.svg" | "/icons/lucide/trending-up.svg" | "/icons/lucide/users.svg" | "/icons/lucide/x.svg" | "/icons/lucide/zap.svg" | "/icons/simple-icons/discord.svg" | "/icons/simple-icons/github.svg" | "/icons/simple-icons/linkedin.svg" | "/icons/simple-icons/x.svg" | "/language-icons/csharp.svg" | "/language-icons/go.svg" | "/language-icons/python.svg" | "/language-icons/rust.svg" | "/language-icons/typescript.svg" | "/logo-black.svg" | "/logo-blue.svg" | "/logo-principal.svg" | "/logo-white.svg" | "/world-map.svg" | string & {};
	}
}