<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
</script>

<section class="wt ts-section" id="why" use:reveal>
  <div class="ts-container">
    <div class="wt__head">
      <h2 class="ts-section-title">{$dict.whyTerraScale.title}</h2>
    </div>

    <ul class="wt__list">
      {#each $dict.whyTerraScale.rows as row (row.title)}
        <li class="wt__row">
          <span class="wt__check">
            <Icon name="check" className="wt__check-icon" />
          </span>
          <div class="wt__copy">
            <h3 class="wt__title">{row.title}</h3>
            <p class="wt__text">{row.text}</p>
          </div>
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .wt {
    border-top: 1px solid var(--ts-divider);
  }

  .wt__head {
    max-width: 680px;
    margin: 0 auto var(--ts-section-gap);
    text-align: center;
  }

  .wt__list {
    list-style: none;
    margin: 0 auto;
    padding: 0;
    max-width: 900px;
    border-top: 1px solid var(--ts-divider);
  }

  .wt__row {
    display: grid;
    grid-template-columns: auto minmax(0, 4fr) minmax(0, 7fr);
    gap: 20px;
    align-items: baseline;
    padding: 18px 4px;
    border-bottom: 1px solid var(--ts-divider);
  }

  .wt__check {
    display: inline-grid;
    place-items: center;
    width: 22px;
    height: 22px;
    border-radius: 4px;
    background: rgba(var(--ts-primary-light-rgb), 0.12);
    color: var(--ts-primary-light);
    align-self: center;
  }

  :global(.wt__check-icon) {
    width: 13px;
    height: 13px;
  }

  .wt__copy {
    display: grid;
    gap: 4px;
  }

  .wt__title {
    margin: 0;
    font-size: 15px;
    line-height: 22px;
    font-weight: 600;
    letter-spacing: -0.01em;
    color: var(--ts-text);
  }

  .wt__text {
    margin: 0;
    font-size: 14px;
    line-height: 1.7;
    color: var(--ts-text-muted);
    text-wrap: pretty;
  }

  @media (max-width: 800px) {
    .wt__row {
      grid-template-columns: auto minmax(0, 1fr);
    }

    .wt__text {
      grid-column: 2;
    }
  }

  @media (max-width: 560px) {
    .wt__row {
      grid-template-columns: auto minmax(0, 1fr);
      gap: 12px;
    }

    .wt__copy {
      gap: 2px;
    }
  }
</style>
