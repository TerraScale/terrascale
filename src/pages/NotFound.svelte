<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
</script>

<section class="ts-section notfound" aria-labelledby="notfound-title">
  <div class="ts-container">
    <div class="ts-empty notfound__box">
      <img class="notfound__mark" src="/icon-blue.svg" alt="" width="40" height="40" />
      <p class="notfound__code" aria-hidden="true">404</p>
      <span class="ts-eyebrow">{$dict.notFound.eyebrow}</span>
      <h1 id="notfound-title" class="notfound__title">{$dict.notFound.title}</h1>
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
  .notfound__box {
    padding-block: var(--ts-s16);
  }

  .notfound__mark {
    width: 40px;
    height: 40px;
  }

  .notfound__code {
    font-family: var(--ts-font-display);
    font-size: var(--ts-fs-5xl);
    font-weight: var(--ts-display-weight);
    letter-spacing: var(--ts-display-tracking);
    line-height: 1;
    color: var(--ts-ink);
  }

  .notfound__title {
    font-size: var(--ts-fs-2xl);
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
