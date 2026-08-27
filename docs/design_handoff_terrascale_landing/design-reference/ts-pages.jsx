function AboutPage() {
  const values = [
    { icon: 'target', title: 'Transparency', text: 'Open communication and honest relationships with every user.' },
    { icon: 'users', title: 'Customer-First', text: "Every decision we make is guided by what's best for our customers." },
    { icon: 'shield', title: 'Innovation', text: 'We continuously push boundaries to deliver cutting-edge solutions.' },
    { icon: 'award', title: 'Always Improving', text: 'We never stop learning, growing, and improving our platform.' },
  ];

  return (
    <React.Fragment>
      <section style={{ position: 'relative', padding: '120px 0 80px', overflow: 'hidden' }}>
        <div style={{
          position: 'absolute', inset: 0, pointerEvents: 'none',
          background: `radial-gradient(ellipse 70% 50% at 50% 0%, var(--ts-hero-glow-top) 0%, transparent 65%)`,
        }} />
        <div style={{
          position: 'absolute', inset: 0, pointerEvents: 'none', opacity: 0.5,
          backgroundImage: `linear-gradient(var(--ts-grid-color) 1px, transparent 1px), linear-gradient(90deg, var(--ts-grid-color) 1px, transparent 1px)`,
          backgroundSize: '32px 32px',
        }} />
        <div style={{ position: 'relative', maxWidth: 920, margin: '0 auto', padding: '0 40px', textAlign: 'center' }}>
          <div className="ts-eyebrow" style={{
            display: 'inline-flex', alignItems: 'center', gap: 8,
            padding: '5px 12px', borderRadius: 4, marginBottom: 24,
            background: 'rgba(var(--ts-primary-rgb), 0.1)',
            border: '1px solid rgba(var(--ts-primary-rgb), 0.25)',
            color: 'var(--ts-primary-light)',
            fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.14em',
            fontFamily: 'var(--ts-font-accent)',
          }}>
            <span style={{ width: 5, height: 5, borderRadius: 3, background: 'var(--ts-primary)' }} />
            About Us
          </div>
          <h1 style={{
            fontSize: 'clamp(2.4rem, 4vw + 1rem, 4.5rem)', fontWeight: 700,
            color: 'var(--ts-text)', letterSpacing: '-0.025em', lineHeight: 1.08, marginBottom: 24,
          }}>
            Database infrastructure<br />
            <span style={{
              background: 'var(--ts-gradient-primary)',
              WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
            }}>built for builders.</span>
          </h1>
          <p style={{ fontSize: 20, color: 'var(--ts-text-muted)', lineHeight: 1.65, maxWidth: 720, margin: '0 auto' }}>
            Our mission is to empower businesses with scalable, secure, and innovative database
            solutions. We believe in transparency, customer-centricity, and continuous improvement.
          </p>
        </div>
      </section>

      <section style={{ padding: '80px 0', background: 'var(--ts-bg-surface)' }}>
        <div style={{ maxWidth: 1240, margin: '0 auto', padding: '0 40px' }}>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 64, alignItems: 'center' }}>
            <div>
              <h2 style={{ fontSize: 40, fontWeight: 700, color: 'var(--ts-text)', letterSpacing: '-0.02em', marginBottom: 20 }}>
                Our Mission
              </h2>
              <p style={{ fontSize: 16, color: 'var(--ts-text-muted)', lineHeight: 1.7, marginBottom: 18 }}>
                At TerraScale, we're on a mission to democratize enterprise-grade database
                infrastructure. Every business, regardless of size, should have access to powerful,
                scalable, secure database solutions.
              </p>
              <p style={{ fontSize: 16, color: 'var(--ts-text-muted)', lineHeight: 1.7, marginBottom: 24 }}>
                Founded on principles of innovation and reliability, we've built a platform that grows
                with your business — so you're never held back by technical limitations.
              </p>
              <blockquote style={{
                margin: 0, padding: '20px 24px',
                background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
                borderLeft: '3px solid var(--ts-primary)', borderRadius: 6,
                fontFamily: 'var(--ts-font-accent)', fontStyle: 'italic',
                fontSize: 15, color: 'var(--ts-text)', lineHeight: 1.6,
              }}>
                "Database technology should empower, not complicate. That's the driving force
                behind everything we build at TerraScale."
              </blockquote>
            </div>

            <div style={{
              position: 'relative', padding: 40, borderRadius: 12,
              background: 'var(--ts-glass-bg)', backdropFilter: 'blur(14px)',
              border: '1px solid var(--ts-glass-border)',
              overflow: 'hidden',
            }}>
              <div style={{
                position: 'absolute', inset: 0,
                background: 'radial-gradient(circle at 30% 20%, rgba(var(--ts-primary-rgb), 0.12) 0%, transparent 60%)',
                pointerEvents: 'none',
              }} />
              <div style={{ position: 'relative', display: 'grid', gap: 16 }}>
                {[
                  { k: 'Founded', v: '2023' },
                  { k: 'Headquarters', v: 'Remote-first' },
                  { k: 'Team', v: '12 engineers' },
                  { k: 'Customers', v: '10,000+' },
                  { k: 'Regions', v: '20+ global' },
                ].map(item => (
                  <div key={item.k} style={{
                    display: 'flex', justifyContent: 'space-between', alignItems: 'center',
                    padding: '14px 16px', borderRadius: 6,
                    background: 'rgba(var(--ts-primary-rgb), 0.04)',
                    border: '1px solid rgba(var(--ts-primary-rgb), 0.12)',
                  }}>
                    <span style={{
                      fontSize: 11, color: 'var(--ts-text-muted)',
                      textTransform: 'uppercase', letterSpacing: '0.12em', fontWeight: 700,
                      fontFamily: 'var(--ts-font-accent)',
                    }}>{item.k}</span>
                    <span style={{ fontSize: 16, color: 'var(--ts-text)', fontWeight: 600 }}>{item.v}</span>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </section>

      <section style={{ padding: '100px 0' }}>
        <div style={{ maxWidth: 1240, margin: '0 auto', padding: '0 40px' }}>
          <SectionHeader eyebrow="Values" title="What we stand for" subtitle="The principles that guide every decision we make." />
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 16, marginTop: 56 }}>
            {values.map(v => (
              <div key={v.title} style={{
                padding: 28, borderRadius: 10,
                background: 'var(--ts-glass-bg)', backdropFilter: 'blur(14px)',
                border: '1px solid var(--ts-glass-border)',
                textAlign: 'center',
              }}>
                <div style={{
                  width: 44, height: 44, borderRadius: 6, margin: '0 auto 16px',
                  background: 'rgba(var(--ts-primary-rgb), 0.14)',
                  border: '1px solid rgba(var(--ts-primary-rgb), 0.3)',
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                }}>
                  <Icon name={v.icon} size={20} color="var(--ts-primary-light)" />
                </div>
                <h3 style={{ fontSize: 17, fontWeight: 700, color: 'var(--ts-text)', marginBottom: 8 }}>{v.title}</h3>
                <p style={{ fontSize: 13, color: 'var(--ts-text-muted)', lineHeight: 1.6, margin: 0 }}>{v.text}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section style={{ padding: '80px 0', background: 'var(--ts-bg-surface)' }}>
        <div style={{ maxWidth: 720, margin: '0 auto', padding: '0 40px', textAlign: 'center' }}>
          <h2 style={{ fontSize: 36, fontWeight: 700, color: 'var(--ts-text)', letterSpacing: '-0.02em', marginBottom: 16 }}>
            Meet the Team
          </h2>
          <p style={{ fontSize: 16, color: 'var(--ts-text-muted)', lineHeight: 1.65, marginBottom: 48 }}>
            Building the future of scalable database technology.
          </p>
          <div style={{
            padding: 40, borderRadius: 12,
            background: 'var(--ts-glass-bg)', backdropFilter: 'blur(14px)',
            border: '1px solid var(--ts-glass-border)',
          }}>
            <div style={{
              width: 92, height: 92, borderRadius: 8, margin: '0 auto 24px',
              background: 'var(--ts-gradient-primary)',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              fontSize: 32, fontWeight: 700, color: '#fff',
              boxShadow: '0 8px 32px rgba(var(--ts-primary-rgb), 0.4)',
            }}>MG</div>
            <div style={{ fontSize: 22, fontWeight: 700, color: 'var(--ts-text)', marginBottom: 4 }}>
              Mario Gabriell Karaziaki Belchior
            </div>
            <div style={{
              fontSize: 12, color: 'var(--ts-primary-light)', fontWeight: 700,
              textTransform: 'uppercase', letterSpacing: '0.14em', marginBottom: 14,
              fontFamily: 'var(--ts-font-accent)',
            }}>
              CEO & Founder
            </div>
            <p style={{ fontSize: 15, color: 'var(--ts-text-muted)', lineHeight: 1.65, margin: 0 }}>
              Building the future of scalable database technology with passion and vision.
              Mario combines deep technical expertise with strategic leadership to drive
              TerraScale's mission forward.
            </p>
          </div>
        </div>
      </section>
    </React.Fragment>
  );
}

function ContactPage() {
  const [formState, setFormState] = useState({ name: '', email: '', company: '', message: '' });
  const [submitted, setSubmitted] = useState(false);

  const handleSubmit = (e) => {
    e.preventDefault();
    setSubmitted(true);
    setTimeout(() => setSubmitted(false), 3200);
  };

  const channels = [
    { icon: 'mail', label: 'Email', value: 'hello@terrascale.tech', href: 'mailto:hello@terrascale.tech' },
    { icon: 'discord', label: 'Discord', value: 'Join our community', href: '#' },
    { icon: 'github', label: 'GitHub', value: 'Open issues & PRs', href: '#' },
    { icon: 'messageSquare', label: 'Support', value: '24/7 priority for Pro', href: '#' },
  ];

  return (
    <React.Fragment>
      <section style={{ position: 'relative', padding: '120px 0 60px', overflow: 'hidden' }}>
        <div style={{
          position: 'absolute', inset: 0, pointerEvents: 'none',
          background: `radial-gradient(ellipse 70% 50% at 50% 0%, var(--ts-hero-glow-top) 0%, transparent 65%)`,
        }} />
        <div style={{
          position: 'absolute', inset: 0, pointerEvents: 'none', opacity: 0.5,
          backgroundImage: `linear-gradient(var(--ts-grid-color) 1px, transparent 1px), linear-gradient(90deg, var(--ts-grid-color) 1px, transparent 1px)`,
          backgroundSize: '32px 32px',
        }} />
        <div style={{ position: 'relative', maxWidth: 840, margin: '0 auto', padding: '0 40px', textAlign: 'center' }}>
          <div style={{
            display: 'inline-flex', alignItems: 'center', gap: 8,
            padding: '5px 12px', borderRadius: 4, marginBottom: 24,
            background: 'rgba(var(--ts-primary-rgb), 0.1)',
            border: '1px solid rgba(var(--ts-primary-rgb), 0.25)',
            color: 'var(--ts-primary-light)',
            fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.14em',
            fontFamily: 'var(--ts-font-accent)',
          }}>
            <Icon name="messageSquare" size={11} />
            Get in Touch
          </div>
          <h1 style={{
            fontSize: 'clamp(2.2rem, 4vw + 1rem, 4rem)', fontWeight: 700,
            color: 'var(--ts-text)', letterSpacing: '-0.025em', lineHeight: 1.1, marginBottom: 20,
          }}>
            Let's build<br />
            <span style={{
              background: 'var(--ts-gradient-primary)',
              WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
            }}>something together.</span>
          </h1>
          <p style={{ fontSize: 18, color: 'var(--ts-text-muted)', lineHeight: 1.65, maxWidth: 600, margin: '0 auto' }}>
            Have questions? Need a custom plan? Want to integrate with TerraScale? We'd love to hear from you.
          </p>
        </div>
      </section>

      <section style={{ padding: '40px 0 100px' }}>
        <div style={{ maxWidth: 1120, margin: '0 auto', padding: '0 40px' }}>
          <div style={{ display: 'grid', gridTemplateColumns: '1.3fr 1fr', gap: 40 }}>

            <form onSubmit={handleSubmit} style={{
              padding: 36, borderRadius: 12,
              background: 'var(--ts-glass-bg)', backdropFilter: 'blur(14px)',
              border: '1px solid var(--ts-glass-border)',
            }}>
              <h2 style={{ fontSize: 22, fontWeight: 700, color: 'var(--ts-text)', marginBottom: 24 }}>
                Send us a message
              </h2>

              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16, marginBottom: 16 }}>
                <FormField label="Name" value={formState.name}
                  onChange={v => setFormState(s => ({ ...s, name: v }))}
                  placeholder="Mario Belchior" />
                <FormField label="Email" type="email" value={formState.email}
                  onChange={v => setFormState(s => ({ ...s, email: v }))}
                  placeholder="mario@acme.com" />
              </div>
              <div style={{ marginBottom: 16 }}>
                <FormField label="Company" value={formState.company}
                  onChange={v => setFormState(s => ({ ...s, company: v }))}
                  placeholder="Acme Inc." />
              </div>
              <div style={{ marginBottom: 24 }}>
                <FormField label="Message" textarea value={formState.message}
                  onChange={v => setFormState(s => ({ ...s, message: v }))}
                  placeholder="Tell us about your project, team size, and what you're hoping to achieve..." />
              </div>

              <button type="submit" style={{
                width: '100%', padding: '14px 24px',
                background: submitted ? 'var(--ts-secondary)' : 'var(--ts-gradient-primary)',
                border: 'none', color: '#fff',
                fontSize: 13, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.08em',
                borderRadius: 6, cursor: 'pointer',
                fontFamily: 'var(--ts-font-accent)',
                display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 8,
                boxShadow: '0 6px 22px rgba(var(--ts-primary-rgb), 0.4)',
                transition: 'all 200ms',
              }}>
                {submitted ? (
                  <React.Fragment>
                    <Icon name="check" size={14} /> Message Sent!
                  </React.Fragment>
                ) : (
                  <React.Fragment>
                    Send Message <Icon name="arrowRight" size={14} />
                  </React.Fragment>
                )}
              </button>

              <div style={{
                marginTop: 20, padding: '12px 16px', borderRadius: 6,
                background: 'rgba(var(--ts-primary-rgb), 0.06)',
                border: '1px solid rgba(var(--ts-primary-rgb), 0.15)',
                fontSize: 12, color: 'var(--ts-text-muted)', lineHeight: 1.5,
                display: 'flex', alignItems: 'center', gap: 10,
              }}>
                <Icon name="shield" size={14} color="var(--ts-primary-light)" />
                Your information is never shared. Replies within 24h.
              </div>
            </form>

            <div>
              <h2 style={{ fontSize: 22, fontWeight: 700, color: 'var(--ts-text)', marginBottom: 20 }}>
                Other ways to reach us
              </h2>
              <div style={{ display: 'grid', gap: 10 }}>
                {channels.map(c => (
                  <a key={c.label} href={c.href} style={{
                    display: 'flex', alignItems: 'center', gap: 14,
                    padding: 18, borderRadius: 8, textDecoration: 'none',
                    background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
                    border: '1px solid var(--ts-glass-border)',
                    transition: 'all 200ms',
                  }}
                  onMouseEnter={e => { e.currentTarget.style.borderColor = 'rgba(var(--ts-primary-rgb), 0.4)'; e.currentTarget.style.transform = 'translateX(3px)'; }}
                  onMouseLeave={e => { e.currentTarget.style.borderColor = 'var(--ts-glass-border)'; e.currentTarget.style.transform = 'none'; }}
                  >
                    <div style={{
                      width: 40, height: 40, borderRadius: 6, flexShrink: 0,
                      background: 'rgba(var(--ts-primary-rgb), 0.14)',
                      border: '1px solid rgba(var(--ts-primary-rgb), 0.3)',
                      display: 'flex', alignItems: 'center', justifyContent: 'center',
                    }}>
                      <Icon name={c.icon} size={18} color="var(--ts-primary-light)" />
                    </div>
                    <div style={{ flex: 1 }}>
                      <div style={{
                        fontSize: 10, color: 'var(--ts-text-muted)',
                        textTransform: 'uppercase', letterSpacing: '0.14em', fontWeight: 700,
                        marginBottom: 3, fontFamily: 'var(--ts-font-accent)',
                      }}>{c.label}</div>
                      <div style={{ fontSize: 14, color: 'var(--ts-text)', fontWeight: 600 }}>{c.value}</div>
                    </div>
                    <Icon name="chevronRight" size={14} color="var(--ts-text-muted)" />
                  </a>
                ))}
              </div>

              <div style={{
                marginTop: 24, padding: 20, borderRadius: 8,
                background: 'linear-gradient(135deg, rgba(var(--ts-primary-rgb), 0.08) 0%, transparent 100%)',
                border: '1px solid rgba(var(--ts-primary-rgb), 0.2)',
              }}>
                <div style={{
                  fontSize: 10, color: 'var(--ts-primary-light)',
                  textTransform: 'uppercase', letterSpacing: '0.14em', fontWeight: 700,
                  marginBottom: 8, fontFamily: 'var(--ts-font-accent)',
                  display: 'inline-flex', alignItems: 'center', gap: 6,
                }}>
                  <span className="pulse-dot" style={{ width: 6, height: 6, borderRadius: 3, background: 'var(--ts-secondary)' }} />
                  Support SLA
                </div>
                <div style={{ fontSize: 14, color: 'var(--ts-text)', lineHeight: 1.55 }}>
                  Pro customers get <strong>priority routing</strong> with 1-hour first response during business hours.
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>
    </React.Fragment>
  );
}

function FormField({ label, value, onChange, placeholder, type = 'text', textarea = false }) {
  const [focused, setFocused] = useState(false);
  const sharedStyle = {
    width: '100%', padding: '11px 14px', borderRadius: 6,
    background: 'rgba(0,0,0,0.25)',
    border: focused ? '1px solid rgba(var(--ts-primary-rgb), 0.5)' : '1px solid var(--ts-glass-border)',
    color: 'var(--ts-text)', fontSize: 14,
    fontFamily: 'Quantico, sans-serif',
    outline: 'none', transition: 'all 150ms',
    boxShadow: focused ? '0 0 0 3px rgba(var(--ts-primary-rgb), 0.12)' : 'none',
  };
  return (
    <div>
      <label style={{
        display: 'block', fontSize: 10, color: 'var(--ts-text-muted)',
        textTransform: 'uppercase', letterSpacing: '0.14em', fontWeight: 700, marginBottom: 6,
        fontFamily: 'var(--ts-font-accent)',
      }}>{label}</label>
      {textarea ? (
        <textarea value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder}
          onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
          rows={5} style={{ ...sharedStyle, resize: 'vertical', minHeight: 120 }} />
      ) : (
        <input type={type} value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder}
          onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
          style={sharedStyle} />
      )}
    </div>
  );
}

Object.assign(window, { AboutPage, ContactPage, FormField });
