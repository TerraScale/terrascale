<script lang="ts">
  import HeroLight from '@/components/HeroLight.svelte'
  import LightFeatures from '@/components/sections/LightFeatures.svelte'
  import HowItWorks from '@/components/sections/HowItWorks.svelte'
  import PricingTeaser from '@/components/sections/PricingTeaser.svelte'
  import FaqSection from '@/components/sections/FaqSection.svelte'
  import CtaSection from '@/components/sections/CtaSection.svelte'
</script>

<div class="home-light">
  <HeroLight />
  <LightFeatures />
  <HowItWorks />
  <PricingTeaser />
  <FaqSection source="home" />
  <CtaSection />
</div>

<style>
  .home-light {
    --ts-bg-base: #f7f8fa;
    --ts-bg-base-raw: 247, 248, 250;
    --ts-bg-surface: #ffffff;
    --ts-bg-card: #ffffff;
    --ts-divider: rgb(16 24 40 / 0.09);
    --ts-glass-bg: #ffffff;
    --ts-glass-border: rgb(16 24 40 / 0.12);
    --ts-grid-color: rgb(33 88 232 / 0.05);
    --ts-text: #0f172a;
    --ts-text-muted: #526071;
    --ts-primary-light: #1a4bd0;
    --ts-primary-light-rgb: 26, 75, 208;
    --ts-secondary: #047857;
    --ts-secondary-rgb: 4, 120, 87;
    --ts-tertiary: #6b46c1;
    --ts-tertiary-rgb: 107, 70, 193;
    --ts-warning: #b45309;
    --ts-success: #047857;
    --ts-success-rgb: 4, 120, 87;
    color-scheme: light;
    background-color: var(--ts-bg-base);
    color: var(--ts-text);
  }

  .home-light :global(.ts-btn-glass:hover) {
    border-color: rgb(16 24 40 / 0.29);
    background: #ffffff;
  }

  .home-light :global(.ts-btn-glass) {
    background: #ffffff;
  }
</style>
