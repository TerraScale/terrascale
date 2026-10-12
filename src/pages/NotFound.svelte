<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
</script>

<section class="notfound" aria-labelledby="notfound-title">
  <div class="notfound__inner">
    <div class="notfound__copy">
      <p class="notfound__eyebrow">{$dict.notFound.eyebrow}<span aria-hidden="true"></span></p>
      <p class="notfound__code" aria-hidden="true">404</p>
      <h1 id="notfound-title" class="notfound__title">{$dict.notFound.title}</h1>
      <p class="notfound__text">{$dict.notFound.text}</p>
      <div class="notfound__actions">
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
    <div class="notfound__art" aria-hidden="true">
      <img src="/404-illustration.webp" alt="" width="900" height="700" decoding="async" />
    </div>
  </div>
</section>

<style>
  .notfound {
    position: relative;
    isolation: isolate;
    overflow: hidden;
    min-height: max(680px, calc(100vh - var(--ts-header-h)));
    min-height: max(680px, calc(100svh - var(--ts-header-h)));
    display: flex;
    align-items: center;
  }

  .notfound__inner {
    width: min(100%, 1514px);
    margin: 0 auto;
    padding: 50px clamp(24px, 5vw, 76px);
    display: grid;
    grid-template-columns: minmax(0, 0.88fr) minmax(0, 1.12fr);
    align-items: center;
  }

  .notfound__copy {
    position: relative;
    z-index: 2;
    max-width: 590px;
  }

  .notfound__eyebrow {
    display: flex;
    align-items: center;
    gap: 14px;
    margin: 0 0 12px;
    color: var(--ts-accent);
    font-family: var(--ts-font-mono);
    font-size: 13px;
    font-weight: 700;
    letter-spacing: 0.13em;
    text-transform: uppercase;
  }

  .notfound__eyebrow span {
    width: 19px;
    height: 3px;
    border-radius: 2px;
    background: var(--ts-accent);
  }

  .notfound__code {
    margin: 0 0 8px;
    color: var(--ts-accent);
    font-family: var(--ts-font-display);
    font-size: clamp(5rem, 9vw, 8rem);
    font-weight: 700;
    line-height: 1;
    letter-spacing: var(--ts-display-tracking);
  }

  .notfound__title {
    margin: 0 0 16px;
    color: var(--ts-ink);
    font-size: clamp(2.3rem, 3.6vw, 3.7rem);
    font-weight: 700;
    letter-spacing: -0.045em;
    line-height: 1.1;
  }

  .notfound__text {
    max-width: 470px;
    margin: 0;
    color: var(--ts-ink-2);
    font-family: var(--ts-font-sans);
    font-size: clamp(1rem, 1.35vw, 1.2rem);
    line-height: 1.6;
  }

  .notfound__actions {
    display: flex;
    flex-wrap: wrap;
    gap: 16px;
    margin-top: 32px;
  }

  :global(.notfound__icon) { width: 16px; height: 16px; }

  .notfound__art {
    position: relative;
    z-index: 1;
    width: min(calc(100% + 60px), 760px);
    justify-self: end;
    margin: -10px -18px -52px -88px;
  }

  .notfound__art img {
    width: 100%;
    height: auto;
  }

  @media (max-width: 900px) {
    .notfound__inner { grid-template-columns: 1fr; padding-top: 66px; padding-bottom: 20px; }
    .notfound__copy { max-width: 680px; }
    .notfound__art { width: min(100%, 620px); margin: -10px auto -30px; justify-self: center; }
  }

  @media (max-width: 560px) {
    .notfound__inner { padding: 32px 24px 0; }
    .notfound__code { font-size: 4.5rem; }
    .notfound__title { font-size: clamp(2rem, 9vw, 3rem); }
    .notfound__actions { gap: 12px; margin-top: 24px; }
    .notfound__art { width: calc(100% + 24px); max-width: none; margin: 0 -12px; }
  }

</style>
