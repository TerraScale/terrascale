<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
</script>

<section class="notfound" aria-labelledby="notfound-title">
  <div class="notfound__floor" aria-hidden="true"></div>
  <div class="notfound__inner">
    <div class="notfound__copy">
      <p class="notfound__eyebrow">{$dict.notFound.eyebrow}<span aria-hidden="true"></span></p>
      <div class="notfound__code" aria-hidden="true">404</div>
      <h1 id="notfound-title" class="notfound__title">{$dict.notFound.title}</h1>
      <p class="notfound__text">{$dict.notFound.text}</p>
      <div class="notfound__actions">
        <RouteLink href="/" class="ts-btn notfound__button notfound__button--primary">
          <Icon name="home" className="notfound__icon" />
          {$dict.notFound.backHome}
        </RouteLink>
        <RouteLink href="/contact" class="ts-btn notfound__button notfound__button--secondary">
          <Icon name="messageSquare" className="notfound__icon" />
          {$dict.notFound.contactSupport}
        </RouteLink>
      </div>
    </div>
    <div class="notfound__art" aria-hidden="true">
      <img src="/404-illustration.webp" alt="" width="680" height="700" decoding="async" />
    </div>
  </div>
</section>

<style>
  .notfound {
    position: relative;
    isolation: isolate;
    overflow: hidden;
    min-height: max(680px, calc(100vh - 72px));
    min-height: max(680px, calc(100svh - 72px));
    display: flex;
    align-items: center;
    background:
      radial-gradient(ellipse 55% 55% at 72% 46%, rgba(var(--ts-primary-rgb), 0.22), transparent 72%),
      radial-gradient(ellipse 46% 42% at 16% 72%, rgba(var(--ts-primary-rgb), 0.16), transparent 74%),
      var(--ts-notfound-bg);
  }

  .notfound__floor {
    position: absolute;
    z-index: -1;
    inset: 58% -18% -42%;
    opacity: 0.48;
    transform: perspective(650px) rotateX(64deg);
    transform-origin: top;
    background-image:
      linear-gradient(var(--ts-notfound-grid) 1px, transparent 1px),
      linear-gradient(90deg, var(--ts-notfound-grid) 1px, transparent 1px);
    background-size: 64px 64px;
    -webkit-mask-image: linear-gradient(to bottom, transparent, black 38%, transparent 92%);
    mask-image: linear-gradient(to bottom, transparent, black 38%, transparent 92%);
    pointer-events: none;
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
    color: var(--ts-primary-light);
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
    background: var(--ts-notfound-blue-bright);
    box-shadow: 0 0 12px var(--ts-notfound-blue-bright);
  }

  .notfound__code {
    margin: 0 0 6px -8px;
    color: var(--ts-notfound-blue);
    font-family: var(--ts-font-body);
    font-size: clamp(7rem, 13vw, 12rem);
    font-weight: 700;
    line-height: 0.94;
    letter-spacing: -0.095em;
    -webkit-text-stroke: 2px var(--ts-notfound-blue-bright);
    text-shadow: 0 0 12px rgba(var(--ts-primary-rgb), 0.95), 0 0 46px rgba(var(--ts-primary-rgb), 0.72);
  }

  .notfound__title {
    margin: 0 0 16px;
    color: var(--ts-text);
    font-size: clamp(2.3rem, 3.6vw, 3.7rem);
    font-weight: 700;
    letter-spacing: -0.045em;
    line-height: 1.1;
  }

  .notfound__text {
    max-width: 470px;
    margin: 0;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: clamp(1rem, 1.35vw, 1.2rem);
    line-height: 1.6;
  }

  .notfound__actions {
    display: flex;
    flex-wrap: wrap;
    gap: 16px;
    margin-top: 32px;
  }

  :global(.notfound__button) {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 12px;
    min-height: 58px;
    padding: 0 25px;
    border: 1px solid var(--ts-notfound-blue-bright);
    border-radius: 12px;
    color: var(--ts-text);
    font-family: var(--ts-font-accent);
    font-size: 16px;
    font-weight: 600;
    letter-spacing: normal;
    text-transform: none;
    text-decoration: none;
    transition: background 180ms ease, box-shadow 180ms ease, transform 180ms ease;
  }

  :global(.notfound__button:hover) { transform: translateY(-2px); }
  :global(.notfound__button--primary) {
    background: var(--ts-notfound-blue);
    box-shadow: 0 0 22px rgba(var(--ts-primary-rgb), 0.58), inset 0 1px rgba(255, 255, 255, 0.22);
  }
  :global(.notfound__button--primary:hover) {
    background: var(--ts-notfound-blue-bright);
    box-shadow: 0 0 30px rgba(var(--ts-primary-rgb), 0.75);
  }
  :global(.notfound__button--secondary) {
    color: var(--ts-primary-light);
    background: rgba(8, 20, 39, 0.58);
  }
  :global(.notfound__button--secondary:hover) { background: rgba(var(--ts-primary-rgb), 0.25); }
  :global(.notfound__icon) { width: 21px; height: 21px; }

  .notfound__art {
    position: relative;
    z-index: 1;
    width: min(100%, 680px);
    justify-self: end;
    margin: -10px -18px -52px -48px;
    filter: drop-shadow(0 26px 36px rgba(0, 4, 17, 0.35));
  }

  .notfound__art img {
    width: 100%;
    height: auto;
    -webkit-mask-image: radial-gradient(ellipse 73% 72% at 48% 48%, black 61%, transparent 100%);
    mask-image: radial-gradient(ellipse 73% 72% at 48% 48%, black 61%, transparent 100%);
  }

  @media (max-width: 900px) {
    .notfound__inner { grid-template-columns: 1fr; padding-top: 66px; padding-bottom: 20px; }
    .notfound__copy { max-width: 680px; }
    .notfound__art { width: min(100%, 620px); margin: -10px auto -30px; justify-self: center; }
  }

  @media (max-width: 560px) {
    .notfound__inner { padding: 46px 24px 0; }
    .notfound__code { font-size: clamp(6.5rem, 32vw, 10rem); }
    .notfound__title { font-size: clamp(2rem, 9vw, 3rem); }
    .notfound__actions { gap: 12px; }
    :global(.notfound__button) { min-height: 52px; padding: 0 17px; font-size: 14px; }
    .notfound__art { width: calc(100% + 48px); max-width: none; margin: 8px -24px -4px; }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.notfound__button) { transition: none; }
  }
</style>
