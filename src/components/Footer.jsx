export function Footer() {
  return (
    <footer className="footer mt-20 flex flex-col items-center gap-6 rounded-t-2xl bg-gradient-to-t from-base-200 to-base-100 p-10 text-base-content shadow-inner">
      <div className="grid w-full max-w-6xl grid-cols-1 items-start gap-10 sm:grid-cols-3">
        <div className="flex flex-col items-center sm:items-start">
          <span className="footer-title mb-3 font-bold text-lg text-primary">
            Company
          </span>
          <ul className="space-y-2">
            <li>
              <a
                className="link link-hover font-medium text-base"
                href="/about"
              >
                About us
              </a>
            </li>
            <li>
              <a
                className="link link-hover font-medium text-base"
                href="/contact"
              >
                Contact
              </a>
            </li>
            <li>
              <a className="link link-hover font-medium text-base" href="#">
                Jobs
              </a>
            </li>
            <li>
              <a className="link link-hover font-medium text-base" href="#">
                Press kit
              </a>
            </li>
          </ul>
        </div>
        <div className="flex flex-col items-center sm:items-start">
          <span className="footer-title mb-3 font-bold text-lg text-primary">
            Legal
          </span>
          <ul className="space-y-2">
            <li>
              <a className="link link-hover font-medium text-base" href="#">
                Terms of use
              </a>
            </li>
            <li>
              <a className="link link-hover font-medium text-base" href="#">
                Privacy policy
              </a>
            </li>
            <li>
              <a className="link link-hover font-medium text-base" href="#">
                Cookie policy
              </a>
            </li>
          </ul>
        </div>
        <div className="flex flex-col items-center">
          <span className="footer-title mb-3 font-bold text-lg text-primary">
            Social
          </span>
          <div className="mt-2 flex gap-5">
            <a
              aria-label="GitHub"
              className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"
              href="https://github.com/terrascale"
              rel="noopener noreferrer"
              target="_blank"
            >
              <span className="icon-[mdi--github] text-2xl text-[#e0e0e0]" />
            </a>
            <a
              aria-label="Discord"
              className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"
              href="https://discord.gg/NrHddv6ZUd"
              rel="noopener noreferrer"
              target="_blank"
            >
              <span className="icon-[ic--baseline-discord] text-2xl text-[#5865F2]" />
            </a>
          </div>
        </div>
      </div>
      <div className="mt-8 flex w-full justify-center border-base-300 border-t pt-4 text-center text-sm opacity-70">
        <p>
          &copy; {new Date().getFullYear()} TerraScale. All rights reserved.
        </p>
      </div>
    </footer>
  );
}
