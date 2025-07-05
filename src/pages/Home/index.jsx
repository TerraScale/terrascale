import { useEffect } from 'preact/hooks';
import preactLogo from '../../assets/preact.svg';

export function Home() {
  useEffect(() => {
    const observer = new window.IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add('animate-fade-in-up');
          }
        });
      },
      { threshold: 0.1 }
    );
    const elements = document.querySelectorAll('.fade-in-up');
    elements.forEach((el) => observer.observe(el));
    return () => observer.disconnect();
  }, []);

  return (
    <div className="home">
      {/* Hero Section */}
      <section className="fade-in-up mb-20 flex min-h-[60vh] animate-fade-in flex-col items-center justify-between gap-12 rounded-3xl bg-gradient-to-br from-primary/10 to-secondary/10 px-6 py-20 shadow-2xl md:flex-row">
        <div className="flex-1 animate-fade-in-left">
          <h1 className="mb-6 bg-gradient-to-r from-primary to-secondary bg-clip-text font-extrabold text-6xl text-transparent leading-tight md:text-7xl">
            Scale Your Tech, Effortlessly
          </h1>
          <p className="mb-10 text-2xl text-base-content/80 md:text-3xl">
            Empowering businesses with secure, scalable, and innovative cloud
            solutions.
          </p>
          <div className="mb-10 flex gap-6">
            <a
              className="btn btn-lg rounded-full border-0 bg-gradient-to-r from-primary to-secondary px-8 text-white shadow-lg"
              href="#features"
            >
              Get Started
            </a>
            <a className="btn btn-lg btn-outline rounded-full px-8" href="#">
              Watch Demo
            </a>
          </div>
        </div>
        <div className="flex flex-1 animate-fade-in-up items-center justify-center">
          <span
            className="icon-[mdi--database] text-[260px] text-primary drop-shadow-lg"
            style={{ color: '#3b82f6' }}
          />
        </div>
      </section>

      {/* Features Section */}
      <section
        className="fade-in-up mx-auto mb-20 max-w-6xl animate-fade-in-up"
        id="features"
      >
        <h2 className="mb-14 bg-gradient-to-r from-primary to-secondary bg-clip-text text-center font-bold text-4xl text-transparent">
          Key Features
        </h2>
        <div className="grid grid-cols-1 gap-10 sm:grid-cols-2 md:grid-cols-2 lg:grid-cols-4">
          <FeatureCard
            desc="Seamlessly connect with your existing tools and workflows."
            icon={
              <svg
                className="h-10 w-10 text-primary"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                viewBox="0 0 24 24"
              >
                <path d="M12 4v16m8-8H4" />
              </svg>
            }
            title="Easy Integration"
          />
          <FeatureCard
            desc="Monitor your data and performance in real time."
            icon={
              <svg
                className="h-10 w-10 text-primary"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                viewBox="0 0 24 24"
              >
                <circle cx="12" cy="12" r="10" />
                <path d="M12 6v6l4 2" />
              </svg>
            }
            title="Real-Time Analytics"
          />
          <FeatureCard
            desc="Enterprise-grade security and 99.99% uptime."
            icon={
              <svg
                className="h-10 w-10 text-primary"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                viewBox="0 0 24 24"
              >
                <rect height="14" rx="2" width="20" x="2" y="5" />
                <path d="M2 10h20" />
              </svg>
            }
            title="Secure & Reliable"
          />
          <FeatureCard
            desc="Grow with confidence—our platform scales with you."
            icon={
              <svg
                className="h-10 w-10 text-primary"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                viewBox="0 0 24 24"
              >
                <path d="M3 12l2-2 4 4 8-8 2 2-10 10z" />
              </svg>
            }
            title="Scalable"
          />
        </div>
      </section>

      {/* Testimonials Section */}
      <section className="fade-in-up mx-auto mb-20 max-w-5xl animate-fade-in-up">
        <h2 className="mb-14 bg-gradient-to-r from-primary to-secondary bg-clip-text text-center font-bold text-4xl text-transparent">
          What Our Users Say
        </h2>
        <div className="grid grid-cols-1 gap-10 md:grid-cols-3">
          <TestimonialCard
            avatar="https://placehold.co/64x64"
            name="Sam Carter"
            text="TerraScale made our cloud migration effortless and secure. Highly recommended!"
            title="CTO, ExampleCorp"
          />
          <TestimonialCard
            avatar="https://placehold.co/64x64"
            name="Priya Patel"
            text="The analytics dashboard is a game changer for our business insights."
            title="Founder, Startly"
          />
          <TestimonialCard
            avatar="https://placehold.co/64x64"
            name="Liam Nguyen"
            text="Excellent support and reliability. We trust TerraScale for all our projects."
            title="Product Manager"
          />
        </div>
        <p className="mt-10 text-center text-base-content/70 text-lg">
          Trusted by 1,000+ users worldwide
        </p>
      </section>

      {/* Bottom CTA Section */}
      <section className="fade-in-up mx-auto max-w-3xl animate-fade-in-up rounded-2xl bg-gradient-to-r from-primary/20 to-secondary/20 py-16 text-center shadow-xl">
        <h2 className="mb-6 font-bold text-4xl">
          Ready to scale your business?
        </h2>
        <a
          className="btn btn-lg rounded-full border-0 bg-gradient-to-r from-primary to-secondary px-10 text-white"
          href="/pricing"
        >
          Start Your Free Trial
        </a>
      </section>
    </div>
  );
}

function FeatureCard({ icon, title, desc }) {
  return (
    <div className="card flex animate-fade-in-up flex-col items-center rounded-2xl border border-base-200 bg-base-100 p-8 text-center shadow-xl transition-transform hover:scale-105">
      <div className="mb-5">{icon}</div>
      <h3 className="mb-2 font-semibold text-xl">{title}</h3>
      <p className="text-base text-base-content/80">{desc}</p>
    </div>
  );
}

function TestimonialCard({ name, title, avatar, text }) {
  return (
    <div className="card flex animate-fade-in-up flex-col items-center rounded-2xl border border-base-200 bg-base-100 p-8 text-center shadow-lg">
      <img
        alt={name}
        className="mb-4 h-20 w-20 rounded-full border-2 border-primary shadow"
        src={avatar}
      />
      <p className="mb-3 text-lg italic">"{text}"</p>
      <div className="font-semibold text-base">{name}</div>
      <div className="text-base-content/60 text-sm">{title}</div>
    </div>
  );
}
