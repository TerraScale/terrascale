export function Footer() {
	return (
		<footer className="footer flex flex-col items-center gap-6 p-10 bg-gradient-to-t from-base-200 to-base-100 text-base-content mt-20 rounded-t-2xl shadow-inner">
			<div className="w-full max-w-6xl grid grid-cols-1 sm:grid-cols-3 gap-10 items-start">
				<div className="flex flex-col items-center sm:items-start">
					<span className="footer-title mb-3 text-lg font-bold text-primary">Company</span>
					<ul className="space-y-2">
						<li><a href="/about" className="link link-hover text-base font-medium">About us</a></li>
						<li><a href="/contact" className="link link-hover text-base font-medium">Contact</a></li>
						<li><a href="#" className="link link-hover text-base font-medium">Jobs</a></li>
						<li><a href="#" className="link link-hover text-base font-medium">Press kit</a></li>
					</ul>
				</div>
				<div className="flex flex-col items-center sm:items-start">
					<span className="footer-title mb-3 text-lg font-bold text-primary">Legal</span>
					<ul className="space-y-2">
						<li><a href="#" className="link link-hover text-base font-medium">Terms of use</a></li>
						<li><a href="#" className="link link-hover text-base font-medium">Privacy policy</a></li>
						<li><a href="#" className="link link-hover text-base font-medium">Cookie policy</a></li>
					</ul>
				</div>
				<div className="flex flex-col items-center">
					<span className="footer-title mb-3 text-lg font-bold text-primary">Social</span>
					<div className="flex gap-5 mt-2">
						<a target="_blank" rel="noopener noreferrer" href="https://github.com/terrascale" aria-label="GitHub" className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10">
							<span className="icon-[mdi--github] text-2xl text-[#e0e0e0]"></span>
						</a>
						<a target="_blank" rel="noopener noreferrer" href="https://discord.gg/NrHddv6ZUd" aria-label="Discord" className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10">
							<span className="icon-[ic--baseline-discord] text-2xl text-[#5865F2]"></span>
						</a>
					</div>
				</div>
			</div>
			<div className="flex w-full justify-center text-center text-sm opacity-70 pt-4 border-t border-base-300 mt-8">
				<p>&copy; {new Date().getFullYear()} TerraScale. All rights reserved.</p>
			</div>
		</footer>
	);
}
