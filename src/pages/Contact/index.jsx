import { useState } from 'preact/hooks';

export function Contact() {
	const [status, setStatus] = useState(null);
	const handleSubmit = (e) => {
		e.preventDefault();
		const form = e.target;
		if (!form.name.value || !form.email.value || !form.message.value) {
			setStatus('error');
			return;
		}
		setStatus('success');
		form.reset();
	};
	return (
		<section className="flex justify-center items-center min-h-[60vh] py-16 bg-gradient-to-br from-primary/10 to-secondary/10 rounded-3xl shadow-2xl animate-fade-in">
			<div className="card w-full max-w-4xl bg-base-100 shadow-2xl border border-base-200">
				<div className="card-body p-10 md:p-16">
					<h1 className="card-title text-5xl md:text-6xl font-extrabold mb-3 bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent text-center w-full justify-center animate-fade-in-down">Contact Us</h1>
					<p className="text-center text-base-content/70 mb-8 text-xl">We'd love to hear from you! Fill out the form or reach us through the info below.</p>
					<div className="grid grid-cols-1 md:grid-cols-2 gap-12 items-start">
						<form className="space-y-6 animate-fade-in-left" action="https://docs.google.com/forms/d/e/YOUR_GOOGLE_FORM_ID/formResponse" method="POST" onSubmit={handleSubmit} aria-label="Contact form">
							<div className="form-control w-full">
								<label className="label font-semibold text-base" htmlFor="name">Name</label>
								<input className="input input-bordered input-lg w-full" type="text" id="name" name="entry.123456" required aria-required="true" placeholder="Your Name" />
							</div>
							<div className="form-control w-full">
								<label className="label font-semibold text-base" htmlFor="email">Email</label>
								<input className="input input-bordered input-lg w-full" type="email" id="email" name="entry.654321" required aria-required="true" placeholder="you@email.com" />
							</div>
							<div className="form-control w-full">
								<label className="label font-semibold text-base" htmlFor="message">Message</label>
								<textarea className="textarea textarea-bordered textarea-lg w-full" id="message" name="entry.222222" rows={4} required aria-required="true" placeholder="How can we help you?"></textarea>
							</div>
							<button type="submit" className="btn btn-primary btn-lg w-full mt-2 shadow-lg">Send Message</button>
							{status === 'success' && <div className="alert alert-success mt-2 animate-fade-in">Message sent! We'll get back to you soon.</div>}
							{status === 'error' && <div className="alert alert-error mt-2 animate-fade-in">Please fill in all fields.</div>}
						</form>
						<div className="space-y-8 text-base md:text-lg bg-base-200/60 rounded-2xl p-8 shadow-lg animate-fade-in-right">
							<div className="flex items-center gap-3">
								<span className="font-semibold">Email:</span>
								<a href="mailto:contact@terrascale.tech" className="link link-primary">contact@terrascale.tech</a>
							</div>
							<div className="flex items-center gap-3">
								<span className="font-semibold">Phone:</span>
								<a href="tel:+15551234567" className="link link-primary">+1 (555) 123-4567</a>
							</div>
							<div className="flex items-center gap-3">
								<span className="font-semibold">Location:</span>
								<span>123 Main St, San Francisco, CA</span>
							</div>
							<div className="flex items-center gap-3">
								<span className="font-semibold">Business Hours:</span>
								<span>Mon-Fri: 9am - 6pm</span>
							</div>
							<div className="flex gap-4 mt-3">
								<a href="https://twitter.com/terrascale" target="_blank" rel="noopener" aria-label="Twitter" className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"><svg width="24" height="24" fill="currentColor" viewBox="0 0 24 24"><path d="M24 4.557a9.93 9.93 0 0 1-2.828.775 4.932 4.932 0 0 0 2.165-2.724c-.951.564-2.005.974-3.127 1.195A4.92 4.92 0 0 0 16.616 3c-2.73 0-4.942 2.21-4.942 4.932 0 .386.045.763.127 1.124C7.728 8.807 4.1 6.884 1.671 3.965c-.423.722-.666 1.561-.666 2.475 0 1.708.87 3.216 2.188 4.099a4.904 4.904 0 0 1-2.237-.616c-.054 2.281 1.581 4.415 3.949 4.89-.386.104-.793.16-1.213.16-.297 0-.583-.028-.862-.08.584 1.823 2.28 3.15 4.29 3.187A9.868 9.868 0 0 1 0 21.543a13.94 13.94 0 0 0 7.548 2.209c9.057 0 14.009-7.496 14.009-13.986 0-.213-.005-.425-.014-.636A9.936 9.936 0 0 0 24 4.557z"/></svg></a>
								<a href="https://linkedin.com/company/terrascale" target="_blank" rel="noopener" aria-label="LinkedIn" className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"><svg width="24" height="24" fill="currentColor" viewBox="0 0 24 24"><path d="M19 0h-14c-2.76 0-5 2.24-5 5v14c0 2.76 2.24 5 5 5h14c2.76 0 5-2.24 5-5v-14c0-2.76-2.24-5-5-5zm-11 19h-3v-10h3v10zm-1.5-11.27c-.97 0-1.75-.79-1.75-1.76s.78-1.76 1.75-1.76 1.75.79 1.75 1.76-.78 1.76-1.75 1.76zm13.5 11.27h-3v-5.6c0-1.34-.03-3.07-1.87-3.07-1.87 0-2.16 1.46-2.16 2.97v5.7h-3v-10h2.89v1.36h.04c.4-.75 1.37-1.54 2.82-1.54 3.01 0 3.57 1.98 3.57 4.56v5.62z"/></svg></a>
								<a href="https://facebook.com/terrascale" target="_blank" rel="noopener" aria-label="Facebook" className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"><svg width="24" height="24" fill="currentColor" viewBox="0 0 24 24"><path d="M22.675 0h-21.35c-.733 0-1.325.592-1.325 1.326v21.348c0 .733.592 1.326 1.325 1.326h11.495v-9.294h-3.128v-3.622h3.128v-2.672c0-3.1 1.893-4.788 4.659-4.788 1.325 0 2.463.099 2.797.143v3.24l-1.918.001c-1.504 0-1.797.715-1.797 1.763v2.313h3.587l-.467 3.622h-3.12v9.294h6.116c.73 0 1.323-.593 1.323-1.326v-21.349c0-.734-.593-1.326-1.326-1.326z"/></svg></a>
							</div>
							<div className="rounded-xl overflow-hidden border border-base-200 mt-4 shadow-md animate-fade-in-up">
								<iframe title="Map" src="https://maps.google.com/maps?q=San+Francisco&t=&z=13&ie=UTF8&iwloc=&output=embed" className="w-full h-32 md:h-40 border-0" loading="lazy"></iframe>
							</div>
						</div>
					</div>
				</div>
			</div>
		</section>
	);
}
