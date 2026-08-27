function PricingSection() {
  const [billing, setBilling] = useState('monthly');

  const plans = [
    {
      name: 'Hobby',
      icon: 'zap',
      accent: 'var(--ts-secondary)',
      accentRgb: 'var(--ts-secondary-rgb)',
      price: { monthly: 4.99, annual: 49 },
      description: 'Great for personal projects and prototypes',
      features: ['3 databases', '1M operations', '10 GB storage', '15 GB egress', 'Priority Discord'],
      cta: 'Start Free Trial',
      popular: false,
    },
    {
      name: 'Pro',
      icon: 'trendingUp',
      accent: 'var(--ts-primary)',
      accentRgb: 'var(--ts-primary-rgb)',
      price: { monthly: 24.99, annual: 249 },
      description: 'Best for growing teams and production',
      features: ['15 databases', '10M operations', '50 GB storage', '100 GB egress', 'Email + Discord', 'Priority support'],
      cta: 'Start Free Trial',
      popular: true,
    },
    {
      name: 'Enterprise',
      icon: 'shieldCheck',
      accent: 'var(--ts-tertiary)',
      accentRgb: 'var(--ts-tertiary-rgb)',
      price: null,
      basePrice: 499,
      description: 'For mission-critical applications',
      features: ['Unlimited databases', 'Custom SLAs', 'Dedicated support', 'On-prem deployment', 'Advanced security', 'Custom integrations'],
      cta: 'Contact Sales',
      popular: false,
    },
  ];

  return (
    <section style={{ padding: '120px 0', position: 'relative' }}>
      <div style={{
        position: 'absolute', inset: 0,
        background: 'radial-gradient(ellipse 60% 50% at 50% 30%, rgba(var(--ts-primary-rgb), 0.08) 0%, transparent 65%)',
        pointerEvents: 'none',
      }} />
      <div style={{ position: 'relative', maxWidth: 1240, margin: '0 auto', padding: '0 40px' }}>
        <SectionHeader
          eyebrow="Pricing"
          title="Simple pricing that scales with you"
          subtitle="Start free, scale on demand. No hidden fees, no surprises."
        />

        <div style={{ display: 'flex', justifyContent: 'center', margin: '40px 0 64px' }}>
          <div style={{
            display: 'inline-flex', position: 'relative',
            background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
            border: '1px solid var(--ts-glass-border)',
            borderRadius: 999, padding: 5,
          }}>
            <div style={{
              position: 'absolute', top: 5, bottom: 5,
              left: billing === 'monthly' ? 5 : 'calc(50% + 0px)',
              width: 'calc(50% - 5px)',
              background: 'var(--ts-gradient-primary)',
              borderRadius: 999, boxShadow: '0 4px 14px rgba(var(--ts-primary-rgb), 0.35)',
              transition: 'left 240ms cubic-bezier(.4,0,.2,1)',
            }} />
            {['monthly', 'annual'].map(b => (
              <button key={b} onClick={() => setBilling(b)} style={{
                position: 'relative', zIndex: 1,
                padding: '11px 30px', border: 'none', background: 'transparent',
                color: billing === b ? '#fff' : 'var(--ts-text-muted)',
                fontSize: 12, fontWeight: 700, cursor: 'pointer',
                textTransform: 'uppercase', letterSpacing: '0.1em',
                transition: 'color 180ms',
                fontFamily: 'var(--ts-font-accent)',
                borderRadius: 999,
              }}>
                {b === 'annual' ? 'Annual' : 'Monthly'}
              </button>
            ))}
            <div style={{
              position: 'absolute',
              top: -11, right: -14,
              display: 'inline-flex', alignItems: 'center', gap: 4,
              padding: '4px 9px', borderRadius: 999,
              background: 'linear-gradient(135deg, #10B981 0%, #059669 100%)',
              color: '#fff', fontSize: 10, fontWeight: 800,
              textTransform: 'uppercase', letterSpacing: '0.1em',
              fontFamily: 'var(--ts-font-accent)',
              boxShadow: '0 4px 12px rgba(16, 185, 129, 0.4)',
              border: '1.5px solid var(--ts-bg-base)',
              whiteSpace: 'nowrap',
              transform: billing === 'annual' ? 'scale(1.05)' : 'scale(1)',
              transition: 'transform 200ms',
            }}>
              <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
                <path d="M5 12l5 5L20 7" />
              </svg>
              Save 20%
            </div>
          </div>
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 20 }}>
          {plans.map(p => <PricingCard key={p.name} plan={p} billing={billing} />)}
        </div>
      </div>
    </section>
  );
}

function PricingCard({ plan, billing }) {
  const [hov, setHov] = useState(false);
  const priceDisplay = plan.price
    ? `$${plan.price[billing]}`
    : 'Custom';
  const priceSuffix = plan.price
    ? billing === 'annual' ? '/year' : '/month'
    : '';

  return (
    <div
      onMouseEnter={() => setHov(true)}
      onMouseLeave={() => setHov(false)}
      style={{
        position: 'relative', padding: 32, borderRadius: 12,
        background: plan.popular
          ? 'linear-gradient(180deg, rgba(var(--ts-primary-rgb), 0.06) 0%, var(--ts-glass-bg) 50%)'
          : 'var(--ts-glass-bg)',
        border: plan.popular
          ? `1.5px solid rgba(${plan.accentRgb}, 0.45)`
          : '1px solid var(--ts-glass-border)',
        backdropFilter: 'blur(14px)',
        transition: 'all 250ms',
        transform: hov ? 'translateY(-4px)' : '',
        boxShadow: hov
          ? `0 20px 48px rgba(${plan.accentRgb}, 0.22)`
          : plan.popular ? `0 8px 30px rgba(${plan.accentRgb}, 0.12)` : 'none',
        display: 'flex', flexDirection: 'column',
      }}>
      {plan.popular && (
        <div style={{
          position: 'absolute', top: -13, left: '50%', transform: 'translateX(-50%)',
          background: plan.accent, color: '#fff',
          padding: '5px 14px', borderRadius: 4,
          fontSize: 10, fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.14em',
          display: 'inline-flex', alignItems: 'center', gap: 6,
          boxShadow: `0 4px 16px rgba(${plan.accentRgb}, 0.4)`,
        }}>
          <Icon name="star" size={11} color="#fff" style={{ fill: '#fff' }} />
          Most Popular
        </div>
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 6 }}>
        <div style={{
          width: 40, height: 40, borderRadius: 6,
          background: `rgba(${plan.accentRgb}, 0.14)`,
          border: `1px solid rgba(${plan.accentRgb}, 0.3)`,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <Icon name={plan.icon} size={18} color={plan.accent} />
        </div>
        <div style={{ fontSize: 22, fontWeight: 700, color: 'var(--ts-text)' }}>{plan.name}</div>
      </div>
      <p style={{ fontSize: 13, color: 'var(--ts-text-muted)', marginBottom: 28, lineHeight: 1.5 }}>
        {plan.description}
      </p>

      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 8 }}>
        <div style={{
          fontSize: 44, fontWeight: 700, lineHeight: 1,
          color: plan.accent,
          letterSpacing: '-0.02em',
        }}>
          {priceDisplay}
        </div>
        {priceSuffix && (
          <div style={{ fontSize: 13, color: 'var(--ts-text-muted)', fontWeight: 500 }}>
            {priceSuffix}
          </div>
        )}
      </div>
      {!plan.price && (
        <div style={{ fontSize: 12, color: 'var(--ts-text-muted)', marginBottom: 8, fontFamily: 'var(--ts-font-mono)' }}>
          starts at ${plan.basePrice}/mo + usage
        </div>
      )}

      <div style={{
        height: 1, width: '100%', margin: '24px 0',
        background: 'linear-gradient(90deg, transparent, var(--ts-glass-border), transparent)',
      }} />

      <ul style={{ listStyle: 'none', padding: 0, margin: '0 0 28px', flex: 1 }}>
        {plan.features.map(f => (
          <li key={f} style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
            <div style={{
              width: 18, height: 18, borderRadius: 4,
              background: `rgba(${plan.accentRgb}, 0.16)`,
              display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
            }}>
              <Icon name="check" size={11} color={plan.accent} />
            </div>
            <span style={{ fontSize: 14, color: 'var(--ts-text)' }}>{f}</span>
          </li>
        ))}
      </ul>

      <button style={{
        width: '100%', padding: '14px 20px',
        background: plan.popular ? plan.accent : 'rgba(255,255,255,0.04)',
        border: plan.popular ? 'none' : '1px solid var(--ts-glass-border)',
        color: plan.popular ? '#fff' : 'var(--ts-text)',
        fontSize: 13, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.08em',
        borderRadius: 6, cursor: 'pointer',
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 8,
        transition: 'all 150ms',
        boxShadow: plan.popular ? `0 6px 18px rgba(${plan.accentRgb}, 0.4)` : 'none',
      }}>
        {plan.cta} <Icon name="arrowRight" size={13} />
      </button>
    </div>
  );
}

function CtaSection() {
  return (
    <section style={{ position: 'relative', padding: '120px 0', overflow: 'hidden' }}>
      <div style={{
        position: 'absolute', inset: 0,
        background: `
          radial-gradient(ellipse 70% 60% at 50% 50%, rgba(var(--ts-primary-rgb), 0.18) 0%, transparent 65%),
          radial-gradient(ellipse 45% 45% at 20% 80%, rgba(var(--ts-secondary-rgb), 0.08) 0%, transparent 60%)
        `,
        pointerEvents: 'none',
      }} />
      <div style={{
        position: 'absolute', inset: 0, opacity: 0.5,
        backgroundImage: `linear-gradient(var(--ts-grid-color) 1px, transparent 1px), linear-gradient(90deg, var(--ts-grid-color) 1px, transparent 1px)`,
        backgroundSize: '30px 30px', pointerEvents: 'none',
      }} />

      <div style={{ position: 'relative', maxWidth: 900, margin: '0 auto', padding: '0 40px', textAlign: 'center' }}>
        <div style={{
          display: 'inline-flex', alignItems: 'center', gap: 8,
          padding: '6px 14px', borderRadius: 4, marginBottom: 24,
          background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
          border: '1px solid var(--ts-glass-border)',
          color: 'var(--ts-text)',
          fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.14em',
        }}>
          <span className="pulse-dot" style={{ width: 6, height: 6, borderRadius: 3, background: 'var(--ts-secondary)', boxShadow: '0 0 8px var(--ts-secondary)' }} />
          Free 30-day trial
        </div>

        <h2 style={{
          fontSize: 'clamp(2rem, 3vw + 0.5rem, 3.5rem)',
          fontWeight: 700, color: 'var(--ts-text)',
          lineHeight: 1.1, letterSpacing: '-0.025em', marginBottom: 22,
        }}>
          Ship your next product<br />
          <span style={{
            background: 'var(--ts-gradient-primary)',
            WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
          }}>on infrastructure that scales.</span>
        </h2>
        <p style={{
          fontSize: 19, color: 'var(--ts-text-muted)', lineHeight: 1.65, maxWidth: 640, margin: '0 auto 40px',
        }}>
          Join thousands of developers deploying to TerraScale. Set up in minutes, scale to millions.
        </p>
        <div style={{ display: 'flex', justifyContent: 'center', gap: 14, flexWrap: 'wrap' }}>
          <a href="#" className="cta-shine" style={{
            display: 'inline-flex', alignItems: 'center', gap: 8,
            background: 'var(--ts-gradient-primary)', color: '#fff',
            fontWeight: 700, fontSize: 13, textTransform: 'uppercase', letterSpacing: '0.08em',
            padding: '16px 36px', borderRadius: 6, textDecoration: 'none',
            boxShadow: '0 6px 22px rgba(var(--ts-primary-rgb), 0.45)',
          }}>
            Start Building Now <Icon name="arrowRight" size={15} />
          </a>
          <a href="#" style={{
            display: 'inline-flex', alignItems: 'center', gap: 8,
            background: 'rgba(255,255,255,0.04)', color: 'var(--ts-text)',
            border: '1px solid rgba(255,255,255,0.12)',
            fontWeight: 700, fontSize: 13, textTransform: 'uppercase', letterSpacing: '0.08em',
            padding: '16px 36px', borderRadius: 6, textDecoration: 'none',
            backdropFilter: 'blur(8px)',
          }}>
            <Icon name="messageSquare" size={14} /> Talk to Sales
          </a>
        </div>
      </div>
    </section>
  );
}

function Footer({ setPage }) {
  const columns = [
    { title: 'Product', links: ['Features', 'Pricing', 'Changelog', 'Roadmap', 'Documentation'] },
    { title: 'Company', links: ['Blog', 'Careers', 'Contact', 'Press Kit'] },
    { title: 'Resources', links: ['Docs', 'API Reference', 'Status', 'Community', 'Support'] },
    { title: 'Legal', links: ['Privacy', 'Terms', 'Security', 'SLA', 'Compliance'] },
  ];

  return (
    <footer style={{
      background: 'var(--ts-bg-surface)',
      borderTop: '1px solid var(--ts-divider)',
      padding: '36px 0 20px',
    }}>
      <div style={{ maxWidth: 1240, margin: '0 auto', padding: '0 40px' }}>
        <div style={{ display: 'grid', gridTemplateColumns: '1.6fr repeat(4, 1fr)', gap: 32, marginBottom: 28 }}>
          <div>
            <div style={{ marginBottom: 12 }}><TerraScaleLogo height={26} /></div>
            <p style={{ fontSize: 13, color: 'var(--ts-text-muted)', lineHeight: 1.5, maxWidth: 320, marginBottom: 14 }}>
              Empowering teams with secure, scalable database infrastructure. Scale your tech, effortlessly.
            </p>
            <div style={{ display: 'flex', gap: 8 }}>
              {[
                {
                  name: 'GitHub', href: 'https://github.com',
                  color: '#F1F5F9', bg: 'rgba(241,245,249,0.08)', border: 'rgba(241,245,249,0.18)',
                  path: 'M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12',
                },
                {
                  name: 'Discord', href: 'https://discord.com',
                  color: '#5865F2', bg: 'rgba(88,101,242,0.12)', border: 'rgba(88,101,242,0.35)',
                  path: 'M20.317 4.369a19.791 19.791 0 0 0-4.885-1.515.074.074 0 0 0-.079.037c-.21.375-.444.864-.608 1.249a18.27 18.27 0 0 0-5.487 0 12.64 12.64 0 0 0-.617-1.25.077.077 0 0 0-.079-.036 19.736 19.736 0 0 0-4.885 1.515.07.07 0 0 0-.032.027C.533 9.045-.319 13.579.099 18.057a.082.082 0 0 0 .031.057 19.9 19.9 0 0 0 5.993 3.03.078.078 0 0 0 .084-.028c.462-.63.874-1.295 1.226-1.994a.076.076 0 0 0-.041-.106 13.107 13.107 0 0 1-1.872-.892.077.077 0 0 1-.008-.128 10.2 10.2 0 0 0 .372-.292.074.074 0 0 1 .077-.01c3.928 1.793 8.18 1.793 12.061 0a.074.074 0 0 1 .078.01c.12.098.246.198.373.292a.077.077 0 0 1-.006.127 12.299 12.299 0 0 1-1.873.891.077.077 0 0 0-.041.107c.36.698.772 1.362 1.225 1.993a.076.076 0 0 0 .084.028 19.839 19.839 0 0 0 6.002-3.03.077.077 0 0 0 .032-.054c.5-5.177-.838-9.674-3.549-13.66a.061.061 0 0 0-.031-.03zM8.02 15.331c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.956-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.956 2.418-2.157 2.418zm7.975 0c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.955-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.946 2.418-2.157 2.418z',
                },
                {
                  name: 'X', href: 'https://x.com',
                  color: '#FFFFFF', bg: 'rgba(255,255,255,0.06)', border: 'rgba(255,255,255,0.18)',
                  path: 'M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z',
                },
                {
                  name: 'LinkedIn', href: 'https://linkedin.com',
                  color: '#0A66C2', bg: 'rgba(10,102,194,0.14)', border: 'rgba(10,102,194,0.38)',
                  path: 'M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 01-2.063-2.065 2.064 2.064 0 112.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z',
                },
                {
                  name: 'Email', href: 'mailto:hello@terrascale.tech',
                  color: '#10B981', bg: 'rgba(16,185,129,0.12)', border: 'rgba(16,185,129,0.35)',
                  path: 'M1.5 8.67v8.58a3 3 0 0 0 3 3h15a3 3 0 0 0 3-3V8.67l-8.928 5.493a3 3 0 0 1-3.144 0L1.5 8.67Z M22.5 6.908V6.75a3 3 0 0 0-3-3h-15a3 3 0 0 0-3 3v.158l9.714 5.978a1.5 1.5 0 0 0 1.572 0L22.5 6.908Z',
                },
              ].map(s => (
                <a key={s.name} href={s.href} target="_blank" rel="noopener noreferrer"
                  aria-label={s.name}
                  className="footer-social"
                  style={{
                    width: 32, height: 32, borderRadius: 7,
                    background: s.bg,
                    border: `1px solid ${s.border}`,
                    display: 'flex', alignItems: 'center', justifyContent: 'center',
                    color: s.color,
                    transition: 'transform 180ms, box-shadow 180ms, background 180ms',
                    textDecoration: 'none',
                  }}
                  onMouseEnter={(e) => {
                    e.currentTarget.style.transform = 'translateY(-2px)';
                    e.currentTarget.style.boxShadow = `0 6px 18px ${s.color}33`;
                  }}
                  onMouseLeave={(e) => {
                    e.currentTarget.style.transform = '';
                    e.currentTarget.style.boxShadow = '';
                  }}
                >
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" style={{ display: 'block' }}>
                    <path d={s.path} fillRule="evenodd" clipRule="evenodd" />
                  </svg>
                </a>
              ))}
            </div>
          </div>

          {columns.map(col => (
            <div key={col.title}>
              <div style={{
                fontSize: 11, fontWeight: 700, color: 'var(--ts-text)',
                textTransform: 'uppercase', letterSpacing: '0.14em', marginBottom: 10,
              }}>{col.title}</div>
              <ul style={{ listStyle: 'none', padding: 0, margin: 0 }}>
                {col.links.map(link => (
                  <li key={link} style={{ marginBottom: 6 }}>
                    <a href="#" style={{
                      fontSize: 12.5, color: 'var(--ts-text-muted)',
                      textDecoration: 'none',
                      display: 'inline-flex', alignItems: 'center', gap: 6,
                      transition: 'color 150ms',
                    }}>
                      <Icon name="chevronRight" size={10} />
                      {link}
                    </a>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>

        <div style={{
          borderTop: '1px solid var(--ts-divider)', paddingTop: 16,
          display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12,
        }}>
          <div style={{ fontSize: 12, color: 'var(--ts-text-muted)' }}>
            © 2026 TerraScale. All rights reserved.
          </div>
          <div style={{ display: 'flex', gap: 20, alignItems: 'center' }}>
            <div style={{
              display: 'inline-flex', alignItems: 'center', gap: 6,
              fontSize: 11, color: 'var(--ts-text-muted)',
              textTransform: 'uppercase', letterSpacing: '0.12em', fontWeight: 600,
            }}>
              <span className="pulse-dot" style={{ width: 6, height: 6, borderRadius: 3, background: 'var(--ts-secondary)', boxShadow: '0 0 6px var(--ts-secondary)' }} />
              Global Regions
            </div>
            <div style={{
              display: 'inline-flex', alignItems: 'center', gap: 6,
              fontSize: 11, color: 'var(--ts-text-muted)',
              textTransform: 'uppercase', letterSpacing: '0.12em', fontWeight: 600,
            }}>
              <Icon name="shield" size={11} />
              Access controls
            </div>
          </div>
        </div>
      </div>
    </footer>
  );
}

Object.assign(window, { PricingSection, PricingCard, CtaSection, Footer });
