
const { useState, useEffect } = React;

function TerraScaleLogo({ height = 40, showWordmark = true }) {
  return (
    <svg viewBox={showWordmark ? "0 0 1429.43 345.24" : "0 0 306 346"}
      style={{ height, width: 'auto', display: 'block' }}>
      <g fill="var(--ts-primary)">
        <polygon points="55.27 116.13 157.01 59.02 106.14 28.79 157.01 0 305.76 87.34 257.3 116.13 205.95 88.78 106.62 147.32 55.27 116.13" />
        <polygon points="257.25 115.54 253.98 232.16 306.05 204.05 305.62 262.48 153.27 345.24 153.47 288.87 203.31 258.87 204.11 143.57 257.25 115.54" />
        <polygon points="153.22 289.26 55.01 231.23 55.16 290.41 .35 261.52 0 89.02 55.58 115.85 55.44 173.96 153.65 229.19 153.22 289.26" />
      </g>
      {showWordmark && (
        <g fill="var(--ts-text)">
          <path d="M450.46,127.06v121.48h-29.32v-121.48h-39.59v-25.13h108.5v25.13h-39.59Z" />
          <path d="M597.92,224.04v24.51h-62.63l-24.3-14.03v-77.71l24.3-14.03h38.33l24.3,14.03v50.06h-58.23v17.18h58.23ZM539.69,167.28v18.01h29.53v-18.01h-29.53Z" />
          <path d="M694.05,168.33h-36.02v80.22h-28.7v-104.73h23.67l5.03,8.59,14.87-8.59h21.15v24.51Z" />
          <path d="M779.72,168.33h-36.02v80.22h-28.7v-104.73h23.67l5.03,8.59,14.87-8.59h21.15v24.51Z" />
          <path d="M820.77,249.59l-24.3-14.03v-36.23l24.3-14.03h33.93v-16.97h-47.75v-24.51h52.15l24.3,14.03v91.74h-13.82l-14.87-8.59-14.87,8.59h-19.06ZM825.17,207.28v18.22h29.53v-18.22h-29.53Z" />
          <path d="M916.91,174.19v-59.27l24.3-14.03h52.99l24.3,14.03v27.86h-29.32v-16.76h-42.94v34.98h47.96l24.3,14.03v60.53l-24.3,14.03h-55.09l-24.3-14.03v-27.86h29.32v16.76h45.03v-36.24h-47.96l-24.3-14.03Z" />
          <path d="M1080.7,168.33v55.71h51.94v24.51h-56.34l-24.3-14.03v-76.66l24.3-14.03h56.34v24.51h-51.94Z" />
          <path d="M1179.98,249.59l-24.3-14.03v-36.23l24.3-14.03h33.93v-16.97h-47.75v-24.51h52.15l24.3,14.03v91.74h-13.82l-14.87-8.59-14.87,8.59h-19.06ZM1184.38,207.28v18.22h29.53v-18.22h-29.53Z" />
          <path d="M1298.32,248.55l-24.3-14.03V95.65h28.7v128.39h20.94v24.51h-25.34Z" />
          <path d="M1429.43,224.04v24.51h-62.63l-24.3-14.03v-77.71l24.3-14.03h38.33l24.3,14.03v50.06h-58.23v17.18h58.23ZM1371.2,167.28v18.01h29.53v-18.01h-29.53Z" />
        </g>
      )}
    </svg>
  );
}

function Icon({ name, size = 16, color = 'currentColor', style: sty = {} }) {
  const paths = {
    arrowRight: 'M5 12h14M12 5l7 7-7 7',
    zap: 'M13 2L3 14h9l-1 10 10-12h-9l1-10z',
    barChart3: 'M18 20V10M12 20V4M6 20v-6',
    shield: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z',
    shieldCheck: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z M9 12l2 2 4-4',
    globe: 'M12 22a10 10 0 100-20 10 10 0 000 20zM2 12h20M12 2c-2.8 3.3-4.3 6.5-4.3 10s1.5 6.7 4.3 10M12 2c2.8 3.3 4.3 6.5 4.3 10s-1.5 6.7-4.3 10',
    database: 'M12 2C6.5 2 2 4 2 6.5v11C2 20 6.5 22 12 22s10-2 10-4.5v-11C22 4 17.5 2 12 2zm0 2c4.4 0 8 1.6 8 2.5S16.4 9 12 9 4 7.4 4 6.5 7.6 4 12 4z',
    check: 'M5 12l5 5L20 7',
    star: 'M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z',
    trendingUp: 'M22 7l-7 7-4-4-9 9',
    messageSquare: 'M21 15a2 2 0 01-2 2H7l-4 4V5a2 2 0 012-2h14a2 2 0 012 2z',
    bookOpen: 'M2 3h6a4 4 0 014 4v14a3 3 0 00-3-3H2V3zm20 0h-6a4 4 0 00-4 4v14a3 3 0 013-3h7V3z',
    code2: 'M9 9L4 12l5 3M15 9l5 3-5 3M12 6l-2 12',
    chevronRight: 'M9 18l6-6-6-6',
    chevronDown: 'M6 9l6 6 6-6',
    menu: 'M3 12h18M3 6h18M3 18h18',
    x: 'M18 6L6 18M6 6l12 12',
    dollarSign: 'M12 1v22M17 5H9.5a3.5 3.5 0 000 7h5a3.5 3.5 0 010 7H6',
    externalLink: 'M18 13v6a2 2 0 01-2 2H5a2 2 0 01-2-2V8a2 2 0 012-2h6M15 3h6v6M10 14L21 3',
    github: 'M9 19c-5 1.5-5-2.5-7-3m14 6v-3.87a3.37 3.37 0 00-.94-2.61c3.14-.35 6.44-1.54 6.44-7A5.44 5.44 0 0020 4.77 5.07 5.07 0 0019.91 1S18.73.65 16 2.48a13.38 13.38 0 00-7 0C6.27.65 5.09 1 5.09 1A5.07 5.07 0 005 4.77a5.44 5.44 0 00-1.5 3.78c0 5.42 3.3 6.61 6.44 7A3.37 3.37 0 009 18.13V22',
    discord: 'M20.317 4.37a19.8 19.8 0 00-4.885-1.515.07.07 0 00-.079.037c-.21.375-.444.864-.608 1.25a18.27 18.27 0 00-5.487 0 12.6 12.6 0 00-.617-1.25.07.07 0 00-.079-.037A19.74 19.74 0 003.677 4.37a.07.07 0 00-.032.027C.533 9.05-.32 13.58.099 18.058a.08.08 0 00.031.055 19.9 19.9 0 005.993 3.03.08.08 0 00.084-.028c.462-.63.874-1.295 1.226-1.994a.076.076 0 00-.041-.106 13.1 13.1 0 01-1.872-.892.077.077 0 01-.008-.128 10.2 10.2 0 00.372-.292.074.074 0 01.077-.01c3.928 1.793 8.18 1.793 12.062 0a.074.074 0 01.078.01c.12.098.246.198.373.292a.077.077 0 01-.006.127 12.3 12.3 0 01-1.873.892.077.077 0 00-.041.107c.36.698.772 1.362 1.225 1.993a.076.076 0 00.084.028 19.84 19.84 0 006.002-3.03.077.077 0 00.032-.054c.5-5.177-.838-9.674-3.549-13.66a.061.061 0 00-.031-.03z',
    target: 'M12 22a10 10 0 100-20 10 10 0 000 20zm0-7a3 3 0 100-6 3 3 0 000 6z',
    users: 'M17 21v-2a4 4 0 00-4-4H5a4 4 0 00-4 4v2M23 21v-2a4 4 0 00-3-3.87M16 3.13a4 4 0 010 7.75',
    award: 'M12 15a7 7 0 100-14 7 7 0 000 14zM8.21 13.89L7 23l5-3 5 3-1.21-9.12',
    mail: 'M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2zm0 2l8 5 8-5',
  };
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none"
      stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
      style={{ display: 'inline-block', flexShrink: 0, ...sty }}>
      <path d={paths[name] || ''} />
    </svg>
  );
}

function Header({ currentPage, setPage }) {
  const [scrolled, setScrolled] = useState(false);
  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 24);
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => window.removeEventListener('scroll', onScroll);
  }, []);

  const navLinks = [
    { id: 'home', label: 'Home' },
    { id: 'pricing', label: 'Pricing' },
    { id: 'contact', label: 'Contact' },
  ];

  return (
    <header style={{
      position: 'sticky', top: 0, zIndex: 100,
      borderBottom: '1px solid var(--ts-divider)',
      background: scrolled ? 'rgba(var(--ts-bg-base-raw), 0.94)' : 'rgba(var(--ts-bg-base-raw), 0.75)',
      backdropFilter: 'blur(20px)', WebkitBackdropFilter: 'blur(20px)',
      transition: 'background 250ms',
    }}>
      <div style={{ maxWidth: 1440, margin: '0 auto', padding: '0 40px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', height: 72 }}>
        <a href="#" onClick={(e) => { e.preventDefault(); setPage('home'); }} style={{ textDecoration: 'none', display: 'flex' }}>
          <TerraScaleLogo height={38} />
        </a>

        <nav style={{ display: 'flex', alignItems: 'center', gap: 2 }}>
          {navLinks.map(link => (
            <a key={link.id} href="#"
              onClick={(e) => { e.preventDefault(); setPage(link.id); window.scrollTo({ top: 0 }); }}
              style={{
                padding: '7px 14px', borderRadius: 4, fontSize: 13, fontWeight: 600,
                transition: 'all 150ms', textDecoration: 'none',
                fontFamily: 'var(--ts-font-accent)',
                color: currentPage === link.id ? 'var(--ts-primary-light)' : 'var(--ts-text-muted)',
                background: currentPage === link.id ? 'rgba(var(--ts-primary-rgb), 0.1)' : 'transparent',
                border: currentPage === link.id ? '1px solid rgba(var(--ts-primary-rgb), 0.25)' : '1px solid transparent',
              }}>{link.label}</a>
          ))}
        </nav>

        <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
          <a href="https://status.terrascale.tech" target="_blank" rel="noopener noreferrer"
             className="status-link"
             style={{
               display: 'flex', alignItems: 'center', gap: 7,
               color: 'var(--ts-text-muted)', fontSize: 12, fontWeight: 500,
               fontFamily: 'var(--ts-font-accent)',
               textDecoration: 'none', transition: 'color 150ms',
             }}
             onMouseEnter={(e) => e.currentTarget.style.color = '#10B981'}
             onMouseLeave={(e) => e.currentTarget.style.color = 'var(--ts-text-muted)'}
          >
            <span className="pulse-dot" style={{ width: 7, height: 7, borderRadius: 4, background: '#10B981', boxShadow: '0 0 7px #10B981', display: 'inline-block' }} />
            Status
          </a>
          <a href="#" className="cta-shine" onClick={(e) => { e.preventDefault(); setPage('contact'); window.scrollTo({ top: 0 }); }} style={{
            display: 'inline-flex', alignItems: 'center', gap: 7,
            background: 'var(--ts-gradient-primary)', color: '#fff',
            fontWeight: 700, fontSize: 12, textTransform: 'uppercase', letterSpacing: '0.07em',
            padding: '9px 20px', borderRadius: 5, textDecoration: 'none',
            fontFamily: 'var(--ts-font-accent)',
            boxShadow: '0 4px 14px rgba(var(--ts-primary-rgb), 0.35)',
          }}>
            Get Started <Icon name="arrowRight" size={13} />
          </a>
        </div>
      </div>
    </header>
  );
}

function HeroSection() {
  return (
    <section style={{ position: 'relative', overflow: 'hidden', minHeight: 'calc(100vh - 72px)', display: 'flex', alignItems: 'center' }}>
      <div className="hero-atmo-overlay" style={{
        background: `
          radial-gradient(ellipse 80% 60% at 50% 0%, var(--ts-hero-glow-top) 0%, transparent 65%),
          radial-gradient(ellipse 55% 45% at 15% 35%, var(--ts-hero-glow-mid) 0%, transparent 55%),
          radial-gradient(ellipse 55% 45% at 85% 60%, var(--ts-hero-glow-mid) 0%, transparent 55%),
          radial-gradient(ellipse 45% 40% at 50% 85%, var(--ts-hero-glow-accent) 0%, transparent 55%)
        `,
      }} />
      <div className="hero-grid-overlay" />
      <div className="hero-scan-overlay" />

      <div style={{ position: 'relative', zIndex: 1, width: '100%', maxWidth: 1240, margin: '0 auto', padding: '60px 40px', textAlign: 'center' }}>

        <div style={{
          display: 'inline-flex', alignItems: 'center', gap: 8, marginBottom: 32,
          padding: '6px 14px', borderRadius: 100,
          background: 'rgba(var(--ts-primary-rgb), 0.08)',
          border: '1px solid rgba(var(--ts-primary-rgb), 0.22)',
          backdropFilter: 'blur(12px)',
          fontFamily: 'var(--ts-font-accent)',
          fontSize: 11, fontWeight: 500, letterSpacing: '0.08em',
          color: 'var(--ts-primary-light)',
          textTransform: 'uppercase',
        }}>
          <span className="pulse-dot" style={{ width: 6, height: 6, borderRadius: 3, background: 'var(--ts-secondary)', boxShadow: '0 0 8px var(--ts-secondary)' }} />
          Database as a Service
        </div>

        <h1 style={{
          fontSize: 'clamp(2.8rem, 5vw + 1rem, 6rem)',
          fontWeight: 700, lineHeight: 1.02, margin: '0 0 24px',
          color: 'var(--ts-text)', letterSpacing: '-0.035em',
        }}>
          Scale your data,<br />
          <span style={{
            background: 'var(--ts-gradient-primary)',
            WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
          }}>effortlessly.</span>
        </h1>

        <p style={{
          fontFamily: 'var(--ts-font-accent)',
          fontSize: 'clamp(1rem, 0.8vw + 0.75rem, 1.2rem)',
          color: 'var(--ts-text-muted)', lineHeight: 1.55, maxWidth: 560,
          margin: '0 auto 44px', fontWeight: 400,
        }}>
          Production-grade database infrastructure with sub-20ms latency.
        </p>

        <div style={{ display: 'flex', gap: 12, justifyContent: 'center', flexWrap: 'wrap' }}>
          <a href="#" className="cta-shine" style={{
            display: 'inline-flex', alignItems: 'center', gap: 8,
            background: 'var(--ts-gradient-primary)', color: '#fff',
            fontWeight: 700, fontSize: 13, textTransform: 'uppercase', letterSpacing: '0.08em',
            padding: '15px 32px', borderRadius: 6, textDecoration: 'none',
            boxShadow: '0 6px 24px rgba(var(--ts-primary-rgb), 0.4)',
          }}>
            Start Free <Icon name="arrowRight" size={14} />
          </a>
          <a href="#" style={{
            display: 'inline-flex', alignItems: 'center', gap: 8,
            background: 'transparent', border: '1px solid var(--ts-glass-border)',
            color: 'var(--ts-text)', fontWeight: 700, fontSize: 13,
            textTransform: 'uppercase', letterSpacing: '0.08em',
            padding: '15px 32px', borderRadius: 6, textDecoration: 'none',
          }}>
            View Docs
          </a>
        </div>

        <div style={{
          marginTop: 96,
          display: 'flex', justifyContent: 'center', gap: 56, flexWrap: 'wrap',
          borderTop: '1px solid var(--ts-glass-border)', paddingTop: 40,
        }}>
          {[
            { v: 'Launch regions', l: 'Uptime' },
            { v: '20ms', l: 'Latency' },
            { v: '1M+', l: 'Queries/sec' },
            { v: '20+', l: 'Regions' },
          ].map(s => (
            <div key={s.l}>
              <div style={{
                fontSize: 32, fontWeight: 700, lineHeight: 1, letterSpacing: '-0.02em',
                background: 'var(--ts-gradient-primary)',
                WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
              }}>{s.v}</div>
              <div style={{
                fontFamily: 'var(--ts-font-accent)',
                fontSize: 10, color: 'var(--ts-text-muted)', marginTop: 6,
                textTransform: 'uppercase', letterSpacing: '0.16em', fontWeight: 500,
              }}>{s.l}</div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function StatCard({ value, label }) {
  const [hov, setHov] = useState(false);
  return (
    <div
      onMouseEnter={() => setHov(true)}
      onMouseLeave={() => setHov(false)}
      style={{
        background: 'var(--ts-glass-bg)', backdropFilter: 'blur(12px)',
        border: '1px solid var(--ts-glass-border)',
        borderLeft: '3px solid var(--ts-primary)',
        borderRadius: 8, padding: '22px 18px', textAlign: 'center',
        position: 'relative', overflow: 'hidden',
        transition: 'transform 200ms, box-shadow 200ms',
        transform: hov ? 'translateY(-3px)' : '',
        boxShadow: hov ? '0 10px 32px rgba(var(--ts-primary-rgb), 0.18)' : '0 2px 8px rgba(0,0,0,0.2)',
      }}>
      <div style={{
        position: 'absolute', inset: 0,
        background: hov ? 'radial-gradient(ellipse at 50% 0%, rgba(var(--ts-primary-rgb), 0.1) 0%, transparent 65%)' : 'none',
        transition: 'background 200ms', pointerEvents: 'none',
      }} />
      <div style={{
        fontSize: 'clamp(1.6rem, 2vw + 0.5rem, 2.4rem)', fontWeight: 700, lineHeight: 1,
        marginBottom: 8,
        background: 'var(--ts-gradient-primary)',
        WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
      }}>{value}</div>
      <div style={{
        fontSize: 11, color: 'var(--ts-text-muted)', textTransform: 'uppercase',
        letterSpacing: '0.1em', fontWeight: 600,
      }}>{label}</div>
    </div>
  );
}

Object.assign(window, { Icon, Header, HeroSection, StatCard, TerraScaleLogo });
