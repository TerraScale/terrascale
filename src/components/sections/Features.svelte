<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import SectionHeader from '@/components/sections/SectionHeader.svelte'
  import type { AppIconName } from '@/lib/icons'

  const features: Array<{
    icon: AppIconName
    title: string
    text: string
    bullets: string[]
  }> = [
    {
      icon: 'zap',
      title: 'Lightning Performance',
      text: 'Sub-20ms query latency with intelligent caching, query optimization, and globally distributed edge replicas that stay close to your users.',
      bullets: ['In-memory caching', 'Query optimization', 'Edge replicas'],
    },
    {
      icon: 'shield',
      title: 'Enterprise Security',
      text: 'SOC 2 Type II, ISO 27001, and HIPAA compliant. End-to-end encryption, private networking, and fine-grained role-based access control by default.',
      bullets: ['SOC 2 Type II', 'E2E encryption', 'RBAC'],
    },
    {
      icon: 'globe',
      title: 'Global Scale',
      text: 'Deploy in 20+ regions with automatic failover, multi-region replication, and a geographic DNS router that serves queries from the nearest node.',
      bullets: ['20+ regions', 'Auto failover', 'Geo routing'],
    },
    {
      icon: 'trendingUp',
      title: 'Auto-scaling',
      text: 'Horizontal and vertical scaling on demand. Zero-downtime resizing, predictive autoscale, and usage-based billing that follows your traffic.',
      bullets: ['Horizontal scale', 'Zero-downtime', 'Usage-based'],
    },
  ]
</script>

<section class="features" id="features">
  <div class="features__inner">
    <SectionHeader
      eyebrow="Platform"
      title="Built for scale, designed for developers"
      subtitle="Every feature engineered for production. No footguns, no surprises, no 3 AM pages."
    />

    <div class="features__grid">
      {#each features as feature (feature.title)}
        <article class="feature-card ts-glass">
          <div class="feature-card__glow" aria-hidden="true"></div>

          <div class="feature-card__icon">
            <Icon name={feature.icon} className="feature-card__icon-svg" />
          </div>

          <h3 class="feature-card__title">{feature.title}</h3>
          <p class="feature-card__text">{feature.text}</p>
          <div class="feature-card__bullets">
            {#each feature.bullets as bullet (bullet)}
              <span class="feature-card__bullet">
                <span class="feature-card__bullet-dot"></span>
                {bullet}
              </span>
            {/each}
          </div>
        </article>
      {/each}
    </div>
  </div>
</section>

<style>
  .features {
    padding: 120px 0;
    position: relative;
  }

  .features__inner {
    position: relative;
    max-width: 1240px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .features__grid {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 20px;
    margin-top: 64px;
  }

  .feature-card {
    position: relative;
    overflow: hidden;
    border-radius: 10px;
    padding: 36px;
    transition:
      transform 250ms ease,
      box-shadow 250ms ease,
      border-color 250ms ease;
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
  }

  .feature-card:hover {
    transform: translateY(-2px);
    box-shadow:
      0 14px 40px rgba(var(--ts-primary-rgb), 0.2),
      inset 0 1px 0 rgba(255, 255, 255, 0.06);
  }

  .feature-card__glow {
    position: absolute;
    top: -60px;
    right: -60px;
    width: 180px;
    height: 180px;
    background: radial-gradient(circle, rgba(var(--ts-primary-rgb), 0.08) 0%, transparent 70%);
    transition: background 250ms ease;
    pointer-events: none;
  }

  .feature-card:hover .feature-card__glow {
    background: radial-gradient(circle, rgba(var(--ts-primary-rgb), 0.25) 0%, transparent 70%);
  }

  .feature-card__icon {
    width: 48px;
    height: 48px;
    border-radius: 6px;
    background: rgba(var(--ts-primary-rgb), 0.14);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    margin-bottom: 24px;
    transition:
      background 260ms ease,
      border-color 260ms ease,
      box-shadow 260ms ease;
    color: var(--ts-primary-light);
  }

  .feature-card:hover .feature-card__icon {
    background: rgba(var(--ts-primary-rgb), 0.22);
    border-color: rgba(var(--ts-primary-rgb), 0.55);
    box-shadow:
      0 0 0 4px rgba(var(--ts-primary-rgb), 0.12),
      0 8px 22px rgba(var(--ts-primary-rgb), 0.28);
  }

  :global(.feature-card__icon-svg) {
    width: 22px;
    height: 22px;
    color: var(--ts-primary-light);
  }

  .feature-card__title {
    font-size: 21px;
    font-weight: 700;
    color: var(--ts-text);
    margin: 0 0 12px;
    letter-spacing: -0.01em;
  }

  .feature-card__text {
    font-size: 15px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    margin: 0 0 20px;
  }

  .feature-card__bullets {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .feature-card__bullet {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 4px 10px;
    border-radius: 4px;
    background: rgba(var(--ts-primary-rgb), 0.08);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.18);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-mono);
    font-size: 11px;
    font-weight: 600;
  }

  .feature-card__bullet-dot {
    width: 4px;
    height: 4px;
    border-radius: 2px;
    background: var(--ts-primary);
  }

  @media (max-width: 860px) {
    .features {
      padding: 80px 0;
    }
    .features__grid {
      grid-template-columns: 1fr;
      gap: 16px;
    }
    .features__inner {
      padding: 0 20px;
    }
    .feature-card {
      padding: 28px;
    }
  }
</style>
