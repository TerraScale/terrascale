<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
  import { launchPhase, registrationPolicyLinks, registrationUrl } from '@/lib/site'

  $: perks = $dict.cta.perks
</script>

<section class="cta">
  <div class="cta__inner">
    <div class="cta__card ts-glass">
      <h2 class="cta__headline">
        {$dict.cta.headlineLead} <span class="ts-text-gradient">{$dict.cta.headlineAccent}</span>
      </h2>
      <p class="cta__text">
        {$dict.cta.text}
      </p>

      <div class="cta__actions">
        <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary cta-shine cta__primary">
          {launchPhase === 'public-alpha' ? $dict.cta.ctaPrimary : $dict.cta.ctaPrelaunch}
          <Icon name="arrowRight" className="cta__icon" />
        </ExternalLink>
        <RouteLink href="/contact" class="ts-btn ts-btn-glass cta__secondary">
          <Icon name="messageSquare" className="cta__icon" />
          {$dict.cta.ctaSecondary}
        </RouteLink>
      </div>

      <nav class="cta__policies" aria-label="Registration policies">
        <span>{$dict.cta.policyLead}</span>
        {#each registrationPolicyLinks as policy (policy.key)}
          <ExternalLink href={policy.href} target="_blank" class="link-wipe">
            {($dict.footer.columns.legal.links as Record<string, string>)[policy.key]}
          </ExternalLink>
        {/each}
      </nav>

      <ul class="cta__perks" aria-label={$dict.cta.perksAria}>
        {#each perks as p (p)}
          <li>
            <Icon name="checkCircle" className="cta__perk-icon" />
            {p}
          </li>
        {/each}
      </ul>
    </div>
  </div>
</section>

<style>
  .cta {
    position: relative;
    padding: 84px 0 112px;
    overflow: hidden;
  }

  .cta__inner {
    position: relative;
    max-width: 960px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .cta__card {
    position: relative;
    border-radius: 16px;
    padding: 48px 40px 40px;
    text-align: center;
    overflow: hidden;
    background:
      radial-gradient(
        ellipse 60% 100% at 50% 0%,
        rgba(var(--ts-primary-rgb), 0.18),
        transparent 70%
      ),
      var(--ts-glass-bg);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.28);
    box-shadow:
      0 18px 48px rgba(var(--ts-primary-rgb), 0.16),
      inset 0 1px 0 rgba(255, 255, 255, 0.05);
  }

  .cta__headline {
    font-size: clamp(2rem, 3vw + 0.5rem, 3.2rem);
    font-weight: 700;
    color: var(--ts-text);
    line-height: 1.12;
    letter-spacing: -0.025em;
    margin: 0 0 16px;
  }

  .cta__text {
    font-family: var(--ts-font-accent);
    font-size: 16px;
    color: var(--ts-text-muted);
    line-height: 1.6;
    max-width: 560px;
    margin: 0 auto 28px;
  }

  .cta__actions {
    display: flex;
    justify-content: center;
    gap: 12px;
    flex-wrap: wrap;
    margin-bottom: 24px;
  }

  :global(.cta__primary) {
    padding: 15px 30px;
    box-shadow: 0 8px 24px rgba(var(--ts-primary-rgb), 0.48);
  }

  :global(.cta__secondary) {
    padding: 15px 28px;
  }

  :global(.cta__icon) {
    width: 14px;
    height: 14px;
  }

  .cta__perks {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    justify-content: center;
    gap: 22px;
    flex-wrap: wrap;
    font-family: var(--ts-font-accent);
    font-size: 12px;
    color: var(--ts-text-muted);
  }

  .cta__policies {
    display: flex;
    justify-content: center;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    margin: 0 0 16px;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: 12px;
  }

  :global(.cta__policies a) {
    color: var(--ts-primary-light);
  }

  .cta__perks li {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-weight: 600;
    letter-spacing: 0.02em;
  }

  :global(.cta__perk-icon) {
    width: 14px;
    height: 14px;
    color: var(--ts-secondary);
  }

  @media (max-width: 640px) {
    .cta {
      padding: 48px 0 72px;
    }
    .cta__inner {
      padding: 0 16px;
    }
    .cta__card {
      padding: 36px 24px 28px;
    }
    .cta__perks {
      gap: 12px;
      font-size: 11px;
    }
  }
</style>
