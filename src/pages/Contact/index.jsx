import { useState } from 'preact/hooks';

export function Contact() {
  const [status, setStatus] = useState(null);
  const handleSubmit = (e) => {
    e.preventDefault();
    const form = e.target;
    if (!(form.name.value && form.email.value && form.message.value)) {
      setStatus('error');
      return;
    }
    setStatus('success');
    form.reset();
  };
  return (
    <section className="flex min-h-[60vh] animate-fade-in items-center justify-center rounded-3xl bg-gradient-to-br from-primary/10 to-secondary/10 py-16 shadow-2xl">
      <div className="card w-full max-w-4xl border border-base-200 bg-base-100 shadow-2xl">
        <div className="card-body p-10 md:p-16">
          <h1 className="card-title mb-3 w-full animate-fade-in-down justify-center bg-gradient-to-r from-primary to-secondary bg-clip-text text-center font-extrabold text-5xl text-transparent md:text-6xl">
            Contact Us
          </h1>
          <p className="mb-8 text-center text-base-content/70 text-xl">
            We'd love to hear from you! Fill out the form or reach us through
            the info below.
          </p>
          <div className="grid grid-cols-1 items-start gap-12 md:grid-cols-2">
            <form
              action="https://docs.google.com/forms/d/e/YOUR_GOOGLE_FORM_ID/formResponse"
              aria-label="Contact form"
              className="animate-fade-in-left space-y-6"
              method="POST"
              onSubmit={handleSubmit}
            >
              <div className="form-control w-full">
                <label className="label font-semibold text-base" htmlFor="name">
                  Name
                </label>
                <input
                  aria-required="true"
                  className="input input-bordered input-lg w-full"
                  id="name"
                  name="entry.123456"
                  placeholder="Your Name"
                  required
                  type="text"
                />
              </div>
              <div className="form-control w-full">
                <label
                  className="label font-semibold text-base"
                  htmlFor="email"
                >
                  Email
                </label>
                <input
                  aria-required="true"
                  className="input input-bordered input-lg w-full"
                  id="email"
                  name="entry.654321"
                  placeholder="you@email.com"
                  required
                  type="email"
                />
              </div>
              <div className="form-control w-full">
                <label
                  className="label font-semibold text-base"
                  htmlFor="message"
                >
                  Message
                </label>
                <textarea
                  aria-required="true"
                  className="textarea textarea-bordered textarea-lg w-full"
                  id="message"
                  name="entry.222222"
                  placeholder="How can we help you?"
                  required
                  rows={4}
                />
              </div>
              <button
                className="btn btn-primary btn-lg mt-2 w-full shadow-lg"
                type="submit"
              >
                Send Message
              </button>
              {status === 'success' && (
                <div className="alert alert-success mt-2 animate-fade-in">
                  Message sent! We'll get back to you soon.
                </div>
              )}
              {status === 'error' && (
                <div className="alert alert-error mt-2 animate-fade-in">
                  Please fill in all fields.
                </div>
              )}
            </form>
            <div className="animate-fade-in-right space-y-8 rounded-2xl bg-base-200/60 p-8 text-base shadow-lg md:text-lg">
              <div className="flex items-center gap-3">
                <span className="font-semibold">Email:</span>
                <a
                  className="link link-primary"
                  href="mailto:contact@terrascale.tech"
                >
                  contact@terrascale.tech
                </a>
              </div>
              <div className="flex items-center gap-3">
                <span className="font-semibold">Phone:</span>
                <a className="link link-primary" href="tel:+15551234567">
                  +1 (555) 123-4567
                </a>
              </div>
              <div className="flex items-center gap-3">
                <span className="font-semibold">Location:</span>
                <span>123 Main St, San Francisco, CA</span>
              </div>
              <div className="flex items-center gap-3">
                <span className="font-semibold">Business Hours:</span>
                <span>Mon-Fri: 9am - 6pm</span>
              </div>
              <div className="mt-3 flex gap-4">
                <a
                  aria-label="Twitter"
                  className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"
                  href="https://twitter.com/terrascale"
                  rel="noopener"
                  target="_blank"
                >
                  <svg
                    fill="currentColor"
                    height="24"
                    viewBox="0 0 24 24"
                    width="24"
                  >
                    <path d="M24 4.557a9.93 9.93 0 0 1-2.828.775 4.932 4.932 0 0 0 2.165-2.724c-.951.564-2.005.974-3.127 1.195A4.92 4.92 0 0 0 16.616 3c-2.73 0-4.942 2.21-4.942 4.932 0 .386.045.763.127 1.124C7.728 8.807 4.1 6.884 1.671 3.965c-.423.722-.666 1.561-.666 2.475 0 1.708.87 3.216 2.188 4.099a4.904 4.904 0 0 1-2.237-.616c-.054 2.281 1.581 4.415 3.949 4.89-.386.104-.793.16-1.213.16-.297 0-.583-.028-.862-.08.584 1.823 2.28 3.15 4.29 3.187A9.868 9.868 0 0 1 0 21.543a13.94 13.94 0 0 0 7.548 2.209c9.057 0 14.009-7.496 14.009-13.986 0-.213-.005-.425-.014-.636A9.936 9.936 0 0 0 24 4.557z" />
                  </svg>
                </a>
                <a
                  aria-label="LinkedIn"
                  className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"
                  href="https://linkedin.com/company/terrascale"
                  rel="noopener"
                  target="_blank"
                >
                  <svg
                    fill="currentColor"
                    height="24"
                    viewBox="0 0 24 24"
                    width="24"
                  >
                    <path d="M19 0h-14c-2.76 0-5 2.24-5 5v14c0 2.76 2.24 5 5 5h14c2.76 0 5-2.24 5-5v-14c0-2.76-2.24-5-5-5zm-11 19h-3v-10h3v10zm-1.5-11.27c-.97 0-1.75-.79-1.75-1.76s.78-1.76 1.75-1.76 1.75.79 1.75 1.76-.78 1.76-1.75 1.76zm13.5 11.27h-3v-5.6c0-1.34-.03-3.07-1.87-3.07-1.87 0-2.16 1.46-2.16 2.97v5.7h-3v-10h2.89v1.36h.04c.4-.75 1.37-1.54 2.82-1.54 3.01 0 3.57 1.98 3.57 4.56v5.62z" />
                  </svg>
                </a>
                <a
                  aria-label="Facebook"
                  className="btn btn-ghost btn-circle btn-lg hover:bg-primary/10"
                  href="https://facebook.com/terrascale"
                  rel="noopener"
                  target="_blank"
                >
                  <svg
                    fill="currentColor"
                    height="24"
                    viewBox="0 0 24 24"
                    width="24"
                  >
                    <path d="M22.675 0h-21.35c-.733 0-1.325.592-1.325 1.326v21.348c0 .733.592 1.326 1.325 1.326h11.495v-9.294h-3.128v-3.622h3.128v-2.672c0-3.1 1.893-4.788 4.659-4.788 1.325 0 2.463.099 2.797.143v3.24l-1.918.001c-1.504 0-1.797.715-1.797 1.763v2.313h3.587l-.467 3.622h-3.12v9.294h6.116c.73 0 1.323-.593 1.323-1.326v-21.349c0-.734-.593-1.326-1.326-1.326z" />
                  </svg>
                </a>
              </div>
              <div className="mt-4 animate-fade-in-up overflow-hidden rounded-xl border border-base-200 shadow-md">
                <iframe
                  className="h-32 w-full border-0 md:h-40"
                  loading="lazy"
                  src="https://maps.google.com/maps?q=San+Francisco&t=&z=13&ie=UTF8&iwloc=&output=embed"
                  title="Map"
                />
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
