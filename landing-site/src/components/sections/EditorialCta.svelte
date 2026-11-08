<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
  import { registrationUrl } from '@/lib/site'
</script>

<section class="ed-cta ts-section" use:reveal>
  <div class="ts-container">
    <div class="ed-cta__inner">
      <h2 class="ed-cta__headline">{$dict.cta.title}</h2>
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary ed-cta__primary">
        {$dict.cta.ctaPrimary}
        <Icon name="arrowRight" className="ed-cta__primary-icon" />
      </ExternalLink>
    </div>
  </div>
</section>

<style>
  .ed-cta__inner {
    display: flex;
    align-items: flex-end;
    justify-content: space-between;
    gap: clamp(24px, 4vw, 64px);
    flex-wrap: wrap;
    padding-block: clamp(24px, 4vw, 56px);
  }

  .ed-cta__headline {
    margin: 0;
    max-width: 16ch;
    font-size: clamp(40px, 5.5vw, 80px);
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.04em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  :global(.ed-cta__primary) {
    height: 48px;
    padding: 0 24px;
    font-size: 15px;
    flex-shrink: 0;
    transition:
      background-color var(--ts-transition-fast),
      transform var(--ts-transition-slow);
  }

  :global(.ed-cta__primary-icon) {
    width: 16px;
    height: 16px;
    transition: transform var(--ts-transition-slow);
  }

  :global(.ed-cta__primary:hover .ed-cta__primary-icon) {
    transform: translateX(3px);
  }

  @media (max-width: 640px) {
    .ed-cta__inner {
      align-items: flex-start;
      flex-direction: column;
    }

    :global(.ed-cta__primary) {
      width: 100%;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.ed-cta__primary-icon),
    :global(.ed-cta__primary:hover .ed-cta__primary-icon) {
      transform: none;
    }
  }
</style>
