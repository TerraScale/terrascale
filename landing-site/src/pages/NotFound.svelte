<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
</script>

<section class="ts-section notfound" aria-labelledby="notfound-title">
  <div class="ts-container">
    <div class="ts-empty notfound__box">
      <span class="ts-icon-tile"><Icon name="search" /></span>
      <span class="ts-eyebrow">404 · {$dict.notFound.eyebrow}</span>
      <h1 id="notfound-title" class="ts-empty-title">{$dict.notFound.title}</h1>
      <p>{$dict.notFound.text}</p>
      <div class="ts-actions notfound__actions">
        <RouteLink href="/" class="ts-btn ts-btn-primary">
          <Icon name="home" className="notfound__icon" />
          {$dict.notFound.backHome}
        </RouteLink>
        <RouteLink href="/contact" class="ts-btn ts-btn-secondary">
          <Icon name="messageSquare" className="notfound__icon" />
          {$dict.notFound.contactSupport}
        </RouteLink>
      </div>
    </div>
  </div>
</section>

<style>
  /* A whole page, not an inline list: the empty state keeps the page height. */
  .notfound__box {
    padding-block: var(--ts-s16);
  }

  .notfound__actions {
    justify-content: center;
    margin-top: var(--ts-s2);
  }

  .notfound :global(.notfound__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }
</style>
