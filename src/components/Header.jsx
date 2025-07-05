import { useLocation } from 'preact-iso';
import { useEffect, useRef, useState } from 'preact/hooks';

export function Header() {
	const { url } = useLocation();
	const themeToggleRef = useRef(null);
	const [theme, setTheme] = useState(() => {
		if (typeof window !== 'undefined') {
			return localStorage.getItem('theme') || 'light';
		}
		return 'light';
	});

	useEffect(() => {
		if (typeof window !== 'undefined') {
			document.documentElement.setAttribute('data-theme', theme);
			localStorage.setItem('theme', theme);
			if (themeToggleRef.current) {
				themeToggleRef.current.checked = theme === 'dark';
			}
		}
	}, [theme]);

	const handleThemeChange = (e) => {
		setTheme(e.target.checked ? 'dark' : 'light');
	};

	return (
		<header className="sticky top-0 z-50 bg-base-100 shadow-lg rounded-b-2xl">
			<nav className="navbar max-w-7xl mx-auto">
				<div className="navbar-start">
					<a href="/" className="flex items-center gap-3 text-2xl font-extrabold bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent">
						{/* Placeholder SVG Logo */}
						<svg width="36" height="36" fill="none" viewBox="0 0 32 32">
							<circle cx="16" cy="16" r="16" fill="url(#g)" />
							<defs>
								<linearGradient id="g" x1="0" y1="0" x2="32" y2="32" gradientUnits="userSpaceOnUse">
									<stop stopColor="#6366F1" />
									<stop offset="1" stopColor="#06B6D4" />
								</linearGradient>
							</defs>
						</svg>
						<span>TerraScale</span>
					</a>
				</div>
				<div className="navbar-center hidden lg:flex">
					<ul className="menu menu-horizontal px-1 gap-2">
						<li>
							<a href="/" className={`rounded-full px-4 py-2 ${url == '/' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>Home</a>
						</li>
						<li>
							<a href="/pricing" className={`rounded-full px-4 py-2 ${url == '/pricing' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>Pricing</a>
						</li>
						<li>
							<a href="/about" className={`rounded-full px-4 py-2 ${url == '/about' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>About</a>
						</li>
						<li>
							<a href="/contact" className={`rounded-full px-4 py-2 ${url == '/contact' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>Contact</a>
						</li>
					</ul>
				</div>
				<div className="navbar-end gap-3">
					<label className="swap swap-rotate">
						<input
							type="checkbox"
							ref={themeToggleRef}
							className="theme-controller hidden"
							value={theme === 'dark' ? 'dark' : 'light'}
							checked={theme === 'dark'}
							onChange={handleThemeChange}
							aria-label="Toggle theme"
						/>
						{/* Sun icon */}
						<svg className="swap-off w-7 h-7" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke="currentColor">
							<path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M12 3v1m0 16v1m8.66-13.66l-.71.71M4.05 19.07l-.71.71M21 12h-1M4 12H3m16.66 5.66l-.71-.71M4.05 4.93l-.71-.71M16 12a4 4 0 11-8 0 4 4 0 018 0z" />
						</svg>
						{/* Moon icon */}
						<svg className="swap-on w-7 h-7" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke="currentColor">
							<path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M21 12.79A9 9 0 1111.21 3a7 7 0 109.79 9.79z" />
						</svg>
					</label>
					<a href="https://app.terrascale.tech" className="btn btn-primary min-w-[110px] rounded-full font-semibold hidden lg:flex">Sign In</a>
					<a href="https://app.terrascale.tech" className="btn btn-outline min-w-[110px] rounded-full font-semibold hidden lg:flex">Dashboard</a>
					
					{/* Mobile Menu */}
					<div className="dropdown dropdown-end lg:hidden">
						<div tabIndex={0} role="button" className="btn btn-ghost btn-circle">
							<svg xmlns="http://www.w3.org/2000/svg" className="h-6 w-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
								<path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 6h16M4 12h16M4 18h16" />
							</svg>
						</div>
						<ul tabIndex={0} className="menu menu-sm dropdown-content bg-base-100 rounded-box z-[1] mt-3 w-56 p-2 shadow">
							<li><a href="/" className={`rounded-full px-4 py-2 ${url == '/' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>Home</a></li>
							<li><a href="/pricing" className={`rounded-full px-4 py-2 ${url == '/pricing' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>Pricing</a></li>
							<li><a href="/about" className={`rounded-full px-4 py-2 ${url == '/about' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>About</a></li>
							<li><a href="/contact" className={`rounded-full px-4 py-2 ${url == '/contact' ? 'bg-gradient-to-r from-primary to-secondary text-white font-semibold shadow' : 'hover:bg-base-200'}`}>Contact</a></li>
							<li className="border-t border-base-300 mt-3 pt-3">
								<a href="https://app.terrascale.tech" className="btn btn-primary btn-sm rounded-full font-semibold w-full mb-2">Sign In</a>
							</li>
							<li>
								<a href="https://app.terrascale.tech" className="btn btn-outline btn-sm rounded-full font-semibold w-full">Dashboard</a>
							</li>
						</ul>
					</div>
				</div>
			</nav>
		</header>
	);
}
