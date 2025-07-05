import preactLogo from '../../assets/preact.svg';
import { useEffect } from 'preact/hooks';

export function Home() {
	useEffect(() => {
		const observer = new window.IntersectionObserver((entries) => {
			entries.forEach(entry => {
				if (entry.isIntersecting) {
					entry.target.classList.add('animate-fade-in-up');
				}
			});
		}, { threshold: 0.1 });
		const elements = document.querySelectorAll('.fade-in-up');
		elements.forEach(el => observer.observe(el));
		return () => observer.disconnect();
	}, []);

	return (
		<div className="home">
			{/* Hero Section */}
			<section className="min-h-[60vh] flex flex-col md:flex-row items-center justify-between gap-12 py-20 px-6 bg-gradient-to-br from-primary/10 to-secondary/10 rounded-3xl shadow-2xl mb-20 fade-in-up animate-fade-in">
				<div className="flex-1 animate-fade-in-left">
					<h1 className="text-6xl md:text-7xl font-extrabold mb-6 bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent leading-tight">Scale Your Tech, Effortlessly</h1>
					<p className="text-2xl md:text-3xl text-base-content/80 mb-10">Empowering businesses with secure, scalable, and innovative cloud solutions.</p>
					<div className="flex gap-6 mb-10">
						<a href="#features" className="btn btn-lg bg-gradient-to-r from-primary to-secondary text-white border-0 shadow-lg rounded-full px-8">Get Started</a>
						<a href="#" className="btn btn-lg btn-outline rounded-full px-8">Watch Demo</a>
					</div>
				</div>
				<div className="flex-1 flex justify-center items-center animate-fade-in-up">
					<span className="icon-[mdi--database] text-[260px] text-primary drop-shadow-lg" style={{ color: '#3b82f6' }}></span>
				</div>
			</section>

			{/* Features Section */}
			<section id="features" className="max-w-6xl mx-auto mb-20 fade-in-up animate-fade-in-up">
				<h2 className="text-4xl font-bold text-center mb-14 bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent">Key Features</h2>
				<div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-2 lg:grid-cols-4 gap-10">
					<FeatureCard icon={<svg className="w-10 h-10 text-primary" fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24"><path d="M12 4v16m8-8H4"/></svg>} title="Easy Integration" desc="Seamlessly connect with your existing tools and workflows." />
					<FeatureCard icon={<svg className="w-10 h-10 text-primary" fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>} title="Real-Time Analytics" desc="Monitor your data and performance in real time." />
					<FeatureCard icon={<svg className="w-10 h-10 text-primary" fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24"><rect width="20" height="14" x="2" y="5" rx="2"/><path d="M2 10h20"/></svg>} title="Secure & Reliable" desc="Enterprise-grade security and 99.99% uptime." />
					<FeatureCard icon={<svg className="w-10 h-10 text-primary" fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24"><path d="M3 12l2-2 4 4 8-8 2 2-10 10z"/></svg>} title="Scalable" desc="Grow with confidence—our platform scales with you." />
				</div>
			</section>

			{/* Testimonials Section */}
			<section className="max-w-5xl mx-auto mb-20 fade-in-up animate-fade-in-up">
				<h2 className="text-4xl font-bold text-center mb-14 bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent">What Our Users Say</h2>
				<div className="grid grid-cols-1 md:grid-cols-3 gap-10">
					<TestimonialCard name="Sam Carter" title="CTO, ExampleCorp" avatar="https://placehold.co/64x64" text="TerraScale made our cloud migration effortless and secure. Highly recommended!" />
					<TestimonialCard name="Priya Patel" title="Founder, Startly" avatar="https://placehold.co/64x64" text="The analytics dashboard is a game changer for our business insights." />
					<TestimonialCard name="Liam Nguyen" title="Product Manager" avatar="https://placehold.co/64x64" text="Excellent support and reliability. We trust TerraScale for all our projects." />
				</div>
				<p className="text-center mt-10 text-base-content/70 text-lg">Trusted by 1,000+ users worldwide</p>
			</section>

			{/* Bottom CTA Section */}
			<section className="max-w-3xl mx-auto text-center py-16 bg-gradient-to-r from-primary/20 to-secondary/20 rounded-2xl shadow-xl fade-in-up animate-fade-in-up">
				<h2 className="text-4xl font-bold mb-6">Ready to scale your business?</h2>
				<a href="/pricing" className="btn btn-lg bg-gradient-to-r from-primary to-secondary text-white border-0 rounded-full px-10">Start Your Free Trial</a>
			</section>
		</div>
	);
}

function FeatureCard({ icon, title, desc }) {
	return (
		<div className="card bg-base-100 shadow-xl p-8 flex flex-col items-center text-center rounded-2xl border border-base-200 hover:scale-105 transition-transform animate-fade-in-up">
			<div className="mb-5">{icon}</div>
			<h3 className="font-semibold text-xl mb-2">{title}</h3>
			<p className="text-base-content/80 text-base">{desc}</p>
		</div>
	);
}

function TestimonialCard({ name, title, avatar, text }) {
	return (
		<div className="card bg-base-100 shadow-lg p-8 flex flex-col items-center text-center rounded-2xl border border-base-200 animate-fade-in-up">
			<img src={avatar} alt={name} className="rounded-full w-20 h-20 mb-4 border-2 border-primary shadow" />
			<p className="italic mb-3 text-lg">"{text}"</p>
			<div className="font-semibold text-base">{name}</div>
			<div className="text-sm text-base-content/60">{title}</div>
		</div>
	);
}
