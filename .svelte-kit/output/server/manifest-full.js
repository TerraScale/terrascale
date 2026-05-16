export const manifest = (() => {
function __memo(fn) {
	let value;
	return () => value ??= (value = fn());
}

return {
	appDir: "_app",
	appPath: "_app",
	assets: new Set(["fonts/jetbrains-mono-bold.woff2","fonts/jetbrains-mono.woff2","fonts/quantico-bold.woff2","fonts/quantico.woff2","icon-black.svg","icon-blue.svg","icon-white.svg","icons/lucide/activity.svg","icons/lucide/alert-circle.svg","icons/lucide/arrow-left.svg","icons/lucide/arrow-right.svg","icons/lucide/award.svg","icons/lucide/bar-chart-3.svg","icons/lucide/book-open.svg","icons/lucide/calendar.svg","icons/lucide/check-circle.svg","icons/lucide/check.svg","icons/lucide/chevron-down.svg","icons/lucide/chevron-right.svg","icons/lucide/clock.svg","icons/lucide/code-2.svg","icons/lucide/database.svg","icons/lucide/dollar-sign.svg","icons/lucide/external-link.svg","icons/lucide/globe.svg","icons/lucide/home.svg","icons/lucide/layout-dashboard.svg","icons/lucide/layout-grid.svg","icons/lucide/mail.svg","icons/lucide/menu.svg","icons/lucide/message-circle.svg","icons/lucide/message-square.svg","icons/lucide/search.svg","icons/lucide/send.svg","icons/lucide/shield-check.svg","icons/lucide/shield.svg","icons/lucide/star.svg","icons/lucide/target.svg","icons/lucide/trending-up.svg","icons/lucide/users.svg","icons/lucide/x.svg","icons/lucide/zap.svg","icons/simple-icons/discord.svg","icons/simple-icons/github.svg","icons/simple-icons/linkedin.svg","icons/simple-icons/x.svg","language-icons/csharp.svg","language-icons/go.svg","language-icons/python.svg","language-icons/rust.svg","language-icons/typescript.svg","logo-black.svg","logo-blue.svg","logo-principal.svg","logo-white.svg","world-map.svg"]),
	mimeTypes: {".woff2":"font/woff2",".svg":"image/svg+xml"},
	_: {
		client: {start:"_app/immutable/entry/start.BOpVRE6D.js",app:"_app/immutable/entry/app.D9RYnfVp.js",imports:["_app/immutable/entry/start.BOpVRE6D.js","_app/immutable/chunks/Kp7fUL91.js","_app/immutable/chunks/l1zXGMTh.js","_app/immutable/entry/app.D9RYnfVp.js","_app/immutable/chunks/l1zXGMTh.js","_app/immutable/chunks/BgUy-a58.js","_app/immutable/chunks/CFKVnMbq.js"],stylesheets:[],fonts:[],uses_env_dynamic_public:false},
		nodes: [
			__memo(() => import('./nodes/0.js')),
			__memo(() => import('./nodes/1.js')),
			__memo(() => import('./nodes/2.js')),
			__memo(() => import('./nodes/3.js')),
			__memo(() => import('./nodes/4.js')),
			__memo(() => import('./nodes/5.js'))
		],
		remotes: {
			
		},
		routes: [
			{
				id: "/",
				pattern: /^\/$/,
				params: [],
				page: { layouts: [0,], errors: [1,], leaf: 2 },
				endpoint: null
			},
			{
				id: "/404",
				pattern: /^\/404\/?$/,
				params: [],
				page: { layouts: [0,], errors: [1,], leaf: 3 },
				endpoint: null
			},
			{
				id: "/contact",
				pattern: /^\/contact\/?$/,
				params: [],
				page: { layouts: [0,], errors: [1,], leaf: 4 },
				endpoint: null
			},
			{
				id: "/pricing",
				pattern: /^\/pricing\/?$/,
				params: [],
				page: { layouts: [0,], errors: [1,], leaf: 5 },
				endpoint: null
			}
		],
		prerendered_routes: new Set([]),
		matchers: async () => {
			
			return {  };
		},
		server_assets: {}
	}
}
})();
