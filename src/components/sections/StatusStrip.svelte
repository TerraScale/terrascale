<script lang="ts">
  import { onMount } from 'svelte'
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'

  const statusUrl = 'https://status.terrascale.tech'
  // Public, CORS-enabled feed behind the status page (range 90d, one entry per day in `days`).
  const feedUrl = `${statusUrl}/api/v1/status`

  type State = 'operational' | 'degraded' | 'partial_outage' | 'major_outage' | 'maintenance'
  const known: string[] = ['operational', 'degraded', 'partial_outage', 'major_outage', 'maintenance']
  const isState = (value: unknown): value is State => typeof value === 'string' && known.includes(value)

  // Prerendered HTML is a neutral link; real data only replaces it once the feed answers.
  let state: State | null = null
  let days: (State | 'none')[] = []

  onMount(async () => {
    try {
      const res = await fetch(feedUrl)
      if (!res.ok) return
      const feed = await res.json()
      if (feed.monitoringDelayed || !isState(feed.status) || feed.range !== '90d' || !Array.isArray(feed.days) || feed.days.length !== 90) return
      days = feed.days.slice(-90).map((d: unknown) => (isState(d) ? d : 'none'))
      state = feed.status
    } catch {
      // Keep the neutral link when the feed is unreachable.
    }
  })

  $: copy = $dict.statusStrip
</script>

<section class="status-strip" aria-label={copy.aria}>
  <ExternalLink href={statusUrl} class="status-strip__panel">
    <span class="status-strip__ring" data-state={state ?? 'none'}>
      <Icon name={state === 'operational' ? 'check' : 'activity'} className="status-strip__ring-icon" />
    </span>
    <span class="status-strip__text">
      <strong>{state ? copy.headline[state] : copy.fallback}</strong>
      <small>{state ? copy.detail : copy.fallbackDetail}</small>
    </span>
    {#if state}
      <span class="status-strip__bars">
        <ul class="status-strip__days" aria-label={copy.barsAria}>
          {#each days as day, i (i)}
            <li data-state={day} title={copy.states[day]}></li>
          {/each}
        </ul>
        <span class="status-strip__axis" aria-hidden="true">
          <span>{copy.axisStart}</span><span>{copy.axisEnd}</span>
        </span>
      </span>
    {/if}
    <span class="status-strip__link">
      {copy.link}
      <Icon name="arrowRight" className="status-strip__link-icon" />
    </span>
  </ExternalLink>
</section>

<style>
  .status-strip {
    padding: var(--ts-space-10) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  :global(.status-strip__panel) {
    display: flex;
    align-items: center;
    gap: var(--ts-space-5);
    min-height: 84px;
    padding: var(--ts-space-5) var(--ts-space-6);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    color: var(--ts-text);
    text-decoration: none;
    transition:
      background-color var(--ts-transition-fast),
      border-color var(--ts-transition-fast);
  }

  :global(.status-strip__panel:hover) {
    background: #222222;
    border-color: var(--ts-glass-border);
  }

  :global(.status-strip__panel:focus-visible) {
    outline: 2px solid var(--ts-primary-light);
    outline-offset: 2px;
  }

  /* Status colours are semantic (success/warning/severe/danger/maintenance); "none" stays neutral. */
  [data-state='operational'] { --state: #6bca92; }
  [data-state='degraded'] { --state: #ffa726; }
  [data-state='partial_outage'] { --state: #ff7a33; }
  [data-state='major_outage'] { --state: #ff5c5c; }
  [data-state='maintenance'] { --state: #b2a0ec; }
  [data-state='none'] { --state: #919191; }

  .status-strip__ring {
    display: grid;
    place-items: center;
    flex: none;
    width: 36px;
    height: 36px;
    border-radius: 50%;
    background: color-mix(in oklab, var(--state) 18%, transparent);
    color: var(--state);
  }

  :global(.status-strip__ring-icon) {
    width: 16px;
    height: 16px;
  }

  .status-strip__text {
    display: grid;
    gap: 2px;
    flex: none;
  }

  .status-strip__text strong {
    font-size: 18px;
    font-weight: 500;
  }

  .status-strip__text small {
    font-size: 14px;
    color: var(--ts-text-muted);
  }

  .status-strip__bars {
    flex: 1;
    min-width: 0;
  }

  .status-strip__days {
    display: grid;
    grid-auto-flow: column;
    grid-auto-columns: 1fr;
    gap: 2px;
    height: 28px;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  .status-strip__days li {
    border-radius: 2px;
    background: var(--state);
  }

  .status-strip__days li[data-state='none'] {
    background: #2c2c2c;
  }

  .status-strip__axis {
    display: flex;
    justify-content: space-between;
    margin-top: 6px;
    font-size: 12px;
    color: var(--ts-text-muted);
  }

  .status-strip__link {
    display: inline-flex;
    align-items: center;
    gap: var(--ts-space-2);
    flex: none;
    margin-left: auto;
    color: var(--ts-primary-light);
    font-weight: 500;
  }

  :global(.status-strip__link-icon) {
    width: 16px;
    height: 16px;
  }

  @media (max-width: 900px) {
    :global(.status-strip__panel) {
      flex-wrap: wrap;
    }

    .status-strip__bars {
      flex-basis: 100%;
      order: 3;
    }
  }

  @media (max-width: 640px) {
    .status-strip {
      padding-block: var(--ts-space-8);
    }

    :global(.status-strip__panel) {
      gap: var(--ts-space-4);
      padding: var(--ts-space-4);
    }

    .status-strip__text strong {
      font-size: 16px;
    }

    .status-strip__text small {
      font-size: 13px;
    }

    .status-strip__link {
      margin-left: 0;
    }
  }
</style>
