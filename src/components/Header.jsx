import { useEffect, useRef, useState } from 'preact/hooks';
import { useLocation } from 'preact-iso';

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
    <header className="sticky top-0 z-50 rounded-b-2xl bg-base-100 shadow-lg">
      <nav className="navbar mx-auto max-w-7xl">
        <div className="navbar-start">
          <a
            className="flex items-center gap-3 bg-gradient-to-r from-primary to-secondary bg-clip-text font-extrabold text-2xl text-transparent"
            href="/"
          >
            {/* Placeholder SVG Logo */}
            <svg fill="none" height="36" viewBox="0 0 32 32" width="36">
              <circle cx="16" cy="16" fill="url(#g)" r="16" />
              <defs>
                <linearGradient
                  gradientUnits="userSpaceOnUse"
                  id="g"
                  x1="0"
                  x2="32"
                  y1="0"
                  y2="32"
                >
                  <stop stopColor="#6366F1" />
                  <stop offset="1" stopColor="#06B6D4" />
                </linearGradient>
              </defs>
            </svg>
            <span>TerraScale</span>
          </a>
        </div>
        <div className="navbar-center hidden lg:flex">
          <ul className="menu menu-horizontal gap-2 px-1">
            <li>
              <a
                className={`rounded-full px-4 py-2 ${url == '/' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                href="/"
              >
                Home
              </a>
            </li>
            <li>
              <a
                className={`rounded-full px-4 py-2 ${url == '/pricing' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                href="/pricing"
              >
                Pricing
              </a>
            </li>
            <li>
              <a
                className={`rounded-full px-4 py-2 ${url == '/about' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                href="/about"
              >
                About
              </a>
            </li>
            <li>
              <a
                className={`rounded-full px-4 py-2 ${url == '/contact' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                href="/contact"
              >
                Contact
              </a>
            </li>
          </ul>
        </div>
        <div className="navbar-end gap-3">
          <label className="swap swap-rotate">
            <input
              aria-label="Toggle theme"
              checked={theme === 'dark'}
              className="theme-controller hidden"
              onChange={handleThemeChange}
              ref={themeToggleRef}
              type="checkbox"
              value={theme === 'dark' ? 'dark' : 'light'}
            />
            {/* Sun icon */}
            <svg
              className="swap-off h-7 w-7"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path
                d="M12 3v1m0 16v1m8.66-13.66l-.71.71M4.05 19.07l-.71.71M21 12h-1M4 12H3m16.66 5.66l-.71-.71M4.05 4.93l-.71-.71M16 12a4 4 0 11-8 0 4 4 0 018 0z"
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2"
              />
            </svg>
            {/* Moon icon */}
            <svg
              className="swap-on h-7 w-7"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path
                d="M21 12.79A9 9 0 1111.21 3a7 7 0 109.79 9.79z"
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2"
              />
            </svg>
          </label>
          <a
            className="btn btn-primary hidden min-w-[110px] rounded-full font-semibold lg:flex"
            href="https://app.terrascale.tech"
          >
            Sign In
          </a>
          <a
            className="btn btn-outline hidden min-w-[110px] rounded-full font-semibold lg:flex"
            href="https://app.terrascale.tech"
          >
            Dashboard
          </a>

          {/* Mobile Menu */}
          <div className="dropdown dropdown-end lg:hidden">
            <div
              className="btn btn-ghost btn-circle"
              role="button"
              tabIndex={0}
            >
              <svg
                className="h-6 w-6"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                xmlns="http://www.w3.org/2000/svg"
              >
                <path
                  d="M4 6h16M4 12h16M4 18h16"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth="2"
                />
              </svg>
            </div>
            <ul
              className="menu menu-sm dropdown-content z-[1] mt-3 w-56 rounded-box bg-base-100 p-2 shadow"
              tabIndex={0}
            >
              <li>
                <a
                  className={`rounded-full px-4 py-2 ${url == '/' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                  href="/"
                >
                  Home
                </a>
              </li>
              <li>
                <a
                  className={`rounded-full px-4 py-2 ${url == '/pricing' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                  href="/pricing"
                >
                  Pricing
                </a>
              </li>
              <li>
                <a
                  className={`rounded-full px-4 py-2 ${url == '/about' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                  href="/about"
                >
                  About
                </a>
              </li>
              <li>
                <a
                  className={`rounded-full px-4 py-2 ${url == '/contact' ? 'bg-gradient-to-r from-primary to-secondary font-semibold text-white shadow' : 'hover:bg-base-200'}`}
                  href="/contact"
                >
                  Contact
                </a>
              </li>
              <li className="mt-3 border-base-300 border-t pt-3">
                <a
                  className="btn btn-primary btn-sm mb-2 w-full rounded-full font-semibold"
                  href="https://app.terrascale.tech"
                >
                  Sign In
                </a>
              </li>
              <li>
                <a
                  className="btn btn-outline btn-sm w-full rounded-full font-semibold"
                  href="https://app.terrascale.tech"
                >
                  Dashboard
                </a>
              </li>
            </ul>
          </div>
        </div>
      </nav>
    </header>
  );
}
