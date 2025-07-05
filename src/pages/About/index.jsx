export function About() {
	return (
		<div className="max-w-4xl mx-auto px-4 py-20 bg-gradient-to-br from-primary/10 to-secondary/10 rounded-3xl shadow-2xl mt-10 animate-fade-in">
			<section className="mb-16 animate-fade-in-down">
				<h1 className="text-5xl font-extrabold mb-4 bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent text-center">About TerraScale</h1>
				<p className="text-xl text-base-content/80 mb-8 text-center">Our mission is to empower businesses with scalable, secure, and innovative technology solutions. We believe in transparency, customer-centricity, and continuous improvement.</p>
			</section>
			<section className="animate-fade-in-right">
				<h2 className="text-2xl font-semibold mb-8 text-center">Our Team</h2>
				<div className="flex justify-center">
					<div className="card bg-base-100 shadow-xl p-8 flex flex-col items-center animate-fade-in-up rounded-2xl border border-base-200">
						<figure className="px-10 pt-6">
							<img src="https://avatars.githubusercontent.com/u/1010107?v=4" alt="Mario Gabriell Karaziaki Belchior" className="rounded-full border-4 border-primary shadow-md w-24 h-24 object-cover" />
						</figure>
						<div className="card-body items-center text-center">
							<h3 className="card-title text-xl font-bold">Mario Gabriell Karaziaki Belchior</h3>
							<p className="font-medium">CEO & Founder</p>
							<p className="text-base text-base-content/60">Building the future of scalable technology with passion and vision.</p>
						</div>
					</div>
				</div>
			</section>
		</div>
	);
}
