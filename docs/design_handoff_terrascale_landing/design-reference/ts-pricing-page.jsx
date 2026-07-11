// ts-pricing-page.jsx — Full Pricing page matching terrascale.tech/pricing

const { useState: usePS } = React;

function PricingPage() {
  return (
    <React.Fragment>
      <PricingSection />
      <PricingFeatureTriad />
      <DetailedPricingTable />
      <PricingFAQ />
      <PricingCTA />
    </React.Fragment>
  );
}

// 3-up feature row (Security / Fast / Auto-scaling)
function PricingFeatureTriad() {
  const items = [
    { icon: 'shieldCheck', title: 'Enterprise Security', text: 'Access-controlled with end-to-end encryption' },
    { icon: 'zap',         title: 'Lightning Fast',      text: 'Sub-20ms query latency worldwide' },
    { icon: 'trendingUp',  title: 'Auto-scaling',        text: 'Scales automatically with your traffic' },
  ];
  return (
    <section style={{ padding: '40px 0 80px' }}>
      <div style={{ maxWidth: 1120, margin: '0 auto', padding: '0 40px' }}>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 16 }}>
          {items.map(it => (
            <div key={it.title} style={{
              display: 'flex', alignItems: 'flex-start', gap: 14,
              padding: 22, borderRadius: 10,
              background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
              border: '1px solid var(--ts-glass-border)',
            }}>
              <div style={{
                width: 40, height: 40, borderRadius: 6, flexShrink: 0,
                background: 'rgba(var(--ts-primary-rgb), 0.14)',
                border: '1px solid rgba(var(--ts-primary-rgb), 0.3)',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}>
                <Icon name={it.icon} size={18} color="var(--ts-primary-light)" />
              </div>
              <div>
                <div style={{ fontSize: 15, fontWeight: 700, color: 'var(--ts-text)', marginBottom: 4 }}>{it.title}</div>
                <div style={{ fontSize: 13, color: 'var(--ts-text-muted)', lineHeight: 1.5, fontFamily: 'var(--ts-font-accent)' }}>{it.text}</div>
              </div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

// Detailed Pricing — usage-based table
function DetailedPricingTable() {
  const rows = [
    { item: 'Enterprise Level Support', price: '$499', unit: '/month',
      details: 'Base platform access for Enterprise deployments and advanced support.' },
    { item: 'Storage Usage', price: '$1', unit: '/GB',
      details: 'Usage-based storage pricing on top of the base platform fee.' },
    { item: 'Database Operations', price: '$1', unit: '/ 1M ops',
      details: 'Scales with workload volume for read, write, and replication activity.' },
  ];

  return (
    <section id="detailed-pricing" style={{ padding: '60px 0', background: 'var(--ts-bg-surface)' }}>
      <div style={{ maxWidth: 1120, margin: '0 auto', padding: '0 40px' }}>
        <div style={{
          display: 'inline-flex', alignItems: 'center', gap: 8,
          padding: '5px 12px', borderRadius: 4, marginBottom: 16,
          background: 'rgba(var(--ts-primary-rgb), 0.1)',
          border: '1px solid rgba(var(--ts-primary-rgb), 0.25)',
          color: 'var(--ts-primary-light)',
          fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.14em',
          fontFamily: 'var(--ts-font-accent)',
        }}>Detailed Pricing</div>

        <h2 style={{ fontSize: 34, fontWeight: 700, color: 'var(--ts-text)', letterSpacing: '-0.02em', marginBottom: 12 }}>
          Clear pricing for custom deployments
        </h2>
        <p style={{
          fontSize: 15, color: 'var(--ts-text-muted)', lineHeight: 1.65, maxWidth: 720, marginBottom: 24,
          fontFamily: 'var(--ts-font-accent)',
        }}>
          Usage-based costs, support scopes, and deployment requirements are broken out below so enterprise teams can estimate spend before talking to sales.
        </p>
        <a href="https://docs.terrascale.tech/pricing" target="_blank" rel="noopener noreferrer" style={{
          display: 'inline-flex', alignItems: 'center', gap: 8,
          padding: '10px 18px', borderRadius: 6,
          background: 'rgba(255,255,255,0.04)', border: '1px solid var(--ts-glass-border)',
          color: 'var(--ts-text)', fontSize: 12, fontWeight: 700,
          textTransform: 'uppercase', letterSpacing: '0.08em', textDecoration: 'none',
          fontFamily: 'var(--ts-font-accent)', marginBottom: 36,
        }}>
          <Icon name="bookOpen" size={13} /> Full Pricing Docs <Icon name="externalLink" size={12} />
        </a>

        <div style={{
          borderRadius: 10, overflow: 'hidden',
          border: '1px solid var(--ts-glass-border)',
          background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
        }}>
          <div style={{
            display: 'grid', gridTemplateColumns: '1.2fr 0.8fr 2fr',
            padding: '14px 24px',
            background: 'rgba(var(--ts-primary-rgb), 0.08)',
            borderBottom: '1px solid var(--ts-glass-border)',
            fontFamily: 'var(--ts-font-accent)',
            fontSize: 10, fontWeight: 700,
            color: 'var(--ts-primary-light)',
            textTransform: 'uppercase', letterSpacing: '0.14em',
          }}>
            <div>Item</div>
            <div>Pricing</div>
            <div>Details</div>
          </div>
          {rows.map((row, i) => (
            <div key={row.item} style={{
              display: 'grid', gridTemplateColumns: '1.2fr 0.8fr 2fr',
              padding: '20px 24px',
              borderBottom: i < rows.length - 1 ? '1px solid var(--ts-glass-border)' : 'none',
              alignItems: 'center',
            }}>
              <div style={{ fontSize: 15, fontWeight: 600, color: 'var(--ts-text)' }}>{row.item}</div>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: 4 }}>
                <span style={{
                  fontSize: 22, fontWeight: 700,
                  background: 'var(--ts-gradient-primary)',
                  WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
                }}>{row.price}</span>
                <span style={{ fontSize: 12, color: 'var(--ts-text-muted)', fontFamily: 'var(--ts-font-accent)' }}>
                  {row.unit}
                </span>
              </div>
              <div style={{ fontSize: 13, color: 'var(--ts-text-muted)', lineHeight: 1.55, fontFamily: 'var(--ts-font-accent)' }}>
                {row.details}
              </div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

// FAQ with collapsible items
const FAQS = [
  { q: 'What is a Database Operation?',
    a: 'A Database Operation is any single read, write, update, or delete action against your database. Each query you execute — whether fetching a row, inserting a record, running an aggregate, or replicating to another region — counts as one or more operations depending on complexity.' },
  { q: 'What if I need more operations or storage?',
    a: 'All plans support overage billing at usage rates ($1 per additional GB of storage, $1 per million additional operations). You\'ll never be cut off mid-request — we\'ll notify you well before you approach limits so you can upgrade or adjust.' },
  { q: 'When can I change my subscription?',
    a: 'Anytime. Upgrades take effect immediately with prorated billing; downgrades apply at the start of your next billing cycle. No lock-in, no cancellation fees.' },
  { q: 'Do you offer custom enterprise solutions?',
    a: 'Yes. For teams with specific compliance requirements, on-premise deployments, custom SLAs, or dedicated infrastructure, our Enterprise plan is fully tailored. Contact sales to scope a deployment that fits your needs.' },
  { q: 'Why do you require a credit card for the free trial?',
    a: 'We require a valid card to prevent abuse and ensure serious usage of our infrastructure. You won\'t be charged during the 30-day trial, and you can cancel anytime. This also makes the transition to a paid plan seamless if you choose to continue.' },
];

function PricingFAQ() {
  const [open, setOpen] = usePS(0);
  return (
    <section style={{ padding: '100px 0' }}>
      <div style={{ maxWidth: 880, margin: '0 auto', padding: '0 40px' }}>
        <div style={{ textAlign: 'center', marginBottom: 48 }}>
          <h2 style={{ fontSize: 38, fontWeight: 700, color: 'var(--ts-text)', letterSpacing: '-0.02em', marginBottom: 12 }}>
            Frequently Asked Questions
          </h2>
          <p style={{ fontSize: 16, color: 'var(--ts-text-muted)', fontFamily: 'var(--ts-font-accent)' }}>
            Get answers to common questions about our pricing and plans.
          </p>
        </div>

        <div style={{ display: 'grid', gap: 10 }}>
          {FAQS.map((faq, i) => (
            <div key={faq.q} style={{
              borderRadius: 8, overflow: 'hidden',
              border: open === i ? '1px solid rgba(var(--ts-primary-rgb), 0.35)' : '1px solid var(--ts-glass-border)',
              background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
              transition: 'border-color 200ms',
            }}>
              <button
                onClick={() => setOpen(open === i ? -1 : i)}
                style={{
                  width: '100%', padding: '18px 22px',
                  display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 16,
                  background: 'transparent', border: 'none', cursor: 'pointer',
                  textAlign: 'left', color: 'var(--ts-text)', fontSize: 15, fontWeight: 600,
                  fontFamily: 'var(--ts-font-body)',
                }}>
                <span>{faq.q}</span>
                <div style={{
                  transform: open === i ? 'rotate(180deg)' : 'rotate(0deg)',
                  transition: 'transform 200ms', flexShrink: 0,
                  color: open === i ? 'var(--ts-primary-light)' : 'var(--ts-text-muted)',
                }}>
                  <Icon name="chevronDown" size={16} />
                </div>
              </button>
              <div style={{
                maxHeight: open === i ? 300 : 0,
                overflow: 'hidden', transition: 'max-height 300ms',
              }}>
                <div style={{
                  padding: '0 22px 20px',
                  fontSize: 14, color: 'var(--ts-text-muted)', lineHeight: 1.65,
                  fontFamily: 'var(--ts-font-accent)',
                }}>{faq.a}</div>
              </div>
            </div>
          ))}
        </div>

        <div style={{
          marginTop: 40, padding: 28, borderRadius: 10, textAlign: 'center',
          background: 'linear-gradient(135deg, rgba(var(--ts-primary-rgb), 0.08) 0%, transparent 100%)',
          border: '1px solid rgba(var(--ts-primary-rgb), 0.2)',
        }}>
          <h3 style={{ fontSize: 18, fontWeight: 700, color: 'var(--ts-text)', marginBottom: 6 }}>
            Still have questions?
          </h3>
          <p style={{ fontSize: 13, color: 'var(--ts-text-muted)', marginBottom: 18, fontFamily: 'var(--ts-font-accent)' }}>
            Our team is here to help you find the perfect plan for your needs.
          </p>
          <div style={{ display: 'flex', gap: 10, justifyContent: 'center', flexWrap: 'wrap' }}>
            <a href="#" className="cta-shine" style={{
              display: 'inline-flex', alignItems: 'center', gap: 7,
              background: 'var(--ts-gradient-primary)', color: '#fff',
              fontWeight: 700, fontSize: 12, textTransform: 'uppercase', letterSpacing: '0.08em',
              padding: '11px 22px', borderRadius: 5, textDecoration: 'none',
              fontFamily: 'var(--ts-font-accent)',
            }}>
              <Icon name="messageSquare" size={13} /> Contact Sales
            </a>
            <a href="https://docs.terrascale.tech" target="_blank" rel="noopener noreferrer" style={{
              display: 'inline-flex', alignItems: 'center', gap: 7,
              background: 'rgba(255,255,255,0.04)', border: '1px solid var(--ts-glass-border)',
              color: 'var(--ts-text)', fontWeight: 700, fontSize: 12,
              textTransform: 'uppercase', letterSpacing: '0.08em',
              padding: '11px 22px', borderRadius: 5, textDecoration: 'none',
              fontFamily: 'var(--ts-font-accent)',
            }}>
              <Icon name="bookOpen" size={13} /> Read Documentation
            </a>
          </div>
        </div>
      </div>
    </section>
  );
}

// Bottom "Ready to get started?" CTA
function PricingCTA() {
  return (
    <section style={{ position: 'relative', padding: '80px 0 100px', overflow: 'hidden' }}>
      <div style={{
        position: 'absolute', inset: 0,
        background: 'radial-gradient(ellipse 60% 50% at 50% 50%, rgba(var(--ts-primary-rgb), 0.12) 0%, transparent 65%)',
        pointerEvents: 'none',
      }} />
      <div style={{ position: 'relative', maxWidth: 720, margin: '0 auto', padding: '0 40px', textAlign: 'center' }}>
        <h2 style={{
          fontSize: 'clamp(2rem, 2.5vw + 1rem, 3rem)', fontWeight: 700,
          color: 'var(--ts-text)', letterSpacing: '-0.025em', lineHeight: 1.12, marginBottom: 14,
        }}>
          Ready to get started?
        </h2>
        <p style={{ fontSize: 17, color: 'var(--ts-text-muted)', marginBottom: 28, fontFamily: 'var(--ts-font-accent)' }}>
          Join thousands of developers building the future with TerraScale.
        </p>
        <a href="#" className="cta-shine" style={{
          display: 'inline-flex', alignItems: 'center', gap: 8,
          background: 'var(--ts-gradient-primary)', color: '#fff',
          fontWeight: 700, fontSize: 13, textTransform: 'uppercase', letterSpacing: '0.08em',
          padding: '15px 34px', borderRadius: 6, textDecoration: 'none',
          fontFamily: 'var(--ts-font-accent)',
          boxShadow: '0 6px 22px rgba(var(--ts-primary-rgb), 0.45)',
        }}>
          Start Building Today <Icon name="arrowRight" size={14} />
        </a>
      </div>
    </section>
  );
}

Object.assign(window, { PricingPage, DetailedPricingTable, PricingFAQ, PricingCTA, PricingFeatureTriad });
