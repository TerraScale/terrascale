<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  type ChannelHrefKind = 'external' | 'mailto' | 'internal'
  type Channel = {
    icon: AppIconName
    label: string
    value: string
    href: string
    kind: ChannelHrefKind
  }

  const channelMeta: Array<{ icon: AppIconName; label: string; href: string; kind: ChannelHrefKind }> = [
    { icon: 'mail', label: 'Email', href: 'mailto:support@terrascale.tech', kind: 'mailto' },
    { icon: 'brandDiscord', label: 'Discord', href: 'https://discord.gg/8Zr2Nw9g', kind: 'external' },
    { icon: 'brandGithub', label: 'GitHub', href: 'https://git.terrascale.tech/TerraScale', kind: 'external' },
  ]
  $: channels = channelMeta.map((meta, i) => ({
    ...meta,
    value: $dict.contact.channels[i].value,
  }))

  let form = { name: '', email: '', company: '', message: '' }

  type SubmitState = 'idle' | 'submitting' | 'success' | 'error'
  let submitState: SubmitState = 'idle'
  let statusMessage = ''
  let submitTimer: ReturnType<typeof setTimeout> | null = null
  const apiBaseUrl = (import.meta.env.PUBLIC_TERRASCALE_API_BASE_URL ?? 'https://api.terrascale.tech').replace(/\/$/, '')

  const setStatus = (state: SubmitState, message = '') => {
    submitState = state
    statusMessage = message
  }

  const clearTimers = () => {
    if (submitTimer) {
      clearTimeout(submitTimer)
      submitTimer = null
    }
  }

  const resetStatusAfter = (delay: number) => {
    if (submitTimer) clearTimeout(submitTimer)
    submitTimer = setTimeout(() => setStatus('idle'), delay)
  }

  const markSuccess = () => {
    clearTimers()
    setStatus('success', $dict.contact.success)
    form = { name: '', email: '', company: '', message: '' }
    resetStatusAfter(6000)
  }

  const handleSubmit = async (event: SubmitEvent) => {
    event.preventDefault()
    if (submitState === 'submitting') return
    clearTimers()
    setStatus('submitting')

    try {
      const company = form.company.trim()
      const response = await fetch(`${apiBaseUrl}/api/v1/public/contact`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          name: form.name,
          email: form.email,
          subject: company ? `Contact from ${company}` : `Contact from ${form.name}`,
          message: form.message,
        }),
      })
      const payload = (await response.json().catch(() => null)) as { accepted?: boolean } | null
      if (!response.ok || payload?.accepted !== true) throw new Error('Contact request was not accepted')
      markSuccess()
    } catch {
      setStatus('error', $dict.contact.errorSend)
      resetStatusAfter(7000)
    }
  }
</script>

<section class="contact-hero">
  <div class="contact-hero__inner">
    <div class="contact-hero__eyebrow">
      <Icon name="messageSquare" className="contact-hero__eyebrow-icon" />
      {$dict.contact.eyebrow}
    </div>
    <h1 class="contact-hero__title">
      {$dict.contact.titleLead}<br />
      <span class="ts-text-gradient">{$dict.contact.titleAccent}</span>
    </h1>
    <p class="contact-hero__subtitle">
      {$dict.contact.subtitle}
    </p>
  </div>
</section>

<section class="contact-body">
  <div class="contact-body__inner">
    <div class="contact-body__grid">
      <form class="contact-form ts-glass" on:submit={handleSubmit}>
        <h2 class="contact-form__title">{$dict.contact.formTitle}</h2>

        <div class="contact-form__row">
          <div>
            <label class="ts-input-label" for="contact-name">{$dict.contact.labels.name}</label>
            <input
              id="contact-name"
              class="ts-input"
              type="text"
              autocomplete="name"
              placeholder={$dict.contact.placeholders.name}
              bind:value={form.name}
              required
            />
          </div>
          <div>
            <label class="ts-input-label" for="contact-email">{$dict.contact.labels.email}</label>
            <input
              id="contact-email"
              class="ts-input"
              type="email"
              autocomplete="email"
              placeholder={$dict.contact.placeholders.email}
              bind:value={form.email}
              required
            />
          </div>
        </div>

        <div class="contact-form__field">
          <label class="ts-input-label" for="contact-company">{$dict.contact.labels.company}</label>
          <input
            id="contact-company"
            class="ts-input"
            type="text"
            autocomplete="organization"
            placeholder={$dict.contact.placeholders.company}
            bind:value={form.company}
          />
        </div>

        <div class="contact-form__field">
          <label class="ts-input-label" for="contact-message">{$dict.contact.labels.message}</label>
          <textarea
            id="contact-message"
            class="ts-input ts-input--textarea"
            rows="5"
            placeholder={$dict.contact.placeholders.message}
            bind:value={form.message}
            required
          ></textarea>
        </div>

        <button
          type="submit"
          class="contact-form__submit"
          class:is-submitting={submitState === 'submitting'}
          class:is-success={submitState === 'success'}
          class:is-error={submitState === 'error'}
          disabled={submitState === 'submitting'}
        >
          {#if submitState === 'submitting'}
            <span class="contact-form__spinner" aria-hidden="true"></span>
            {$dict.contact.submit.submitting}
          {:else if submitState === 'success'}
            <Icon name="check" className="contact-form__submit-icon" />
            {$dict.contact.submit.success}
          {:else if submitState === 'error'}
            <Icon name="alertCircle" className="contact-form__submit-icon" />
            {$dict.contact.submit.error}
          {:else}
            {$dict.contact.submit.idle}
            <Icon name="arrowRight" className="contact-form__submit-icon" />
          {/if}
        </button>

        {#if statusMessage}
          <p
            class="contact-form__status"
            class:is-error={submitState === 'error'}
            role="status"
            aria-live="polite"
          >
            {statusMessage}
          </p>
        {/if}

        <div class="contact-form__note">
          <Icon name="shield" className="contact-form__note-icon" />
          {$dict.contact.note}
        </div>

        {#if submitState === 'success'}
          <div class="contact-form__badge" aria-hidden="true">
            <span class="contact-form__badge-icon">
              <Icon name="check" className="contact-form__badge-check" />
            </span>
            {$dict.contact.submit.badge}
          </div>
        {/if}
      </form>

      <div class="contact-channels">
        <h2 class="contact-channels__title">{$dict.contact.channelsTitle}</h2>
        <div class="contact-channels__list">
          {#each channels as c (c.label)}
            {#if c.kind === 'external'}
              <ExternalLink
                href={c.href}
                target="_blank"
                class="contact-channel ts-glass"
              >
                <div class="contact-channel__icon">
                  <Icon name={c.icon} className="contact-channel__icon-svg" />
                </div>
                <div class="contact-channel__body">
                  <div class="contact-channel__label">{c.label}</div>
                  <div class="contact-channel__value">{c.value}</div>
                </div>
                <Icon name="chevronRight" className="contact-channel__chev" />
              </ExternalLink>
            {:else}
              <ExternalLink href={c.href} class="contact-channel ts-glass">
                <div class="contact-channel__icon">
                  <Icon name={c.icon} className="contact-channel__icon-svg" />
                </div>
                <div class="contact-channel__body">
                  <div class="contact-channel__label">{c.label}</div>
                  <div class="contact-channel__value">{c.value}</div>
                </div>
                <Icon name="chevronRight" className="contact-channel__chev" />
              </ExternalLink>
            {/if}
          {/each}
        </div>

      </div>
    </div>
  </div>
</section>

<style>
  .contact-hero {
    position: relative;
    padding: 120px 0 60px;
    overflow: hidden;
  }

  .contact-hero__inner {
    position: relative;
    max-width: 840px;
    margin: 0 auto;
    padding: 0 40px;
    text-align: center;
  }

  .contact-hero__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    margin-bottom: 24px;
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  :global(.contact-hero__eyebrow-icon) {
    width: 11px;
    height: 11px;
  }

  .contact-hero__title {
    font-size: clamp(2.2rem, 4vw + 1rem, 4rem);
    font-weight: 700;
    color: var(--ts-text);
    letter-spacing: -0.025em;
    line-height: 1.1;
    margin: 0 0 20px;
  }

  .contact-hero__subtitle {
    font-family: var(--ts-font-accent);
    font-size: 18px;
    color: var(--ts-text-muted);
    line-height: 1.65;
    max-width: 600px;
    margin: 0 auto;
  }

  .contact-body {
    padding: 40px 0 100px;
  }

  .contact-body__inner {
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .contact-body__grid {
    display: grid;
    grid-template-columns: 1.3fr 1fr;
    gap: 40px;
  }

  .contact-form {
    position: relative;
    padding: 36px;
    border-radius: 12px;
  }

  .contact-form__title {
    font-size: 22px;
    font-weight: 700;
    color: var(--ts-text);
    margin: 0 0 24px;
  }

  .contact-form__row {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 16px;
    margin-bottom: 16px;
  }

  .contact-form__field {
    margin-bottom: 16px;
  }

  .contact-form__field:last-of-type {
    margin-bottom: 24px;
  }

  .contact-form__submit {
    width: 100%;
    padding: 14px 24px;
    background: var(--ts-gradient-primary);
    border: none;
    color: #fff;
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    border-radius: 6px;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    box-shadow: 0 6px 22px rgba(var(--ts-primary-rgb), 0.4);
    transition:
      transform 180ms ease,
      background 200ms ease,
      box-shadow 180ms ease;
  }

  .contact-form__submit:hover:not(:disabled) {
    transform: translateY(-1px);
    box-shadow: 0 12px 32px rgba(var(--ts-primary-rgb), 0.55);
  }

  .contact-form__submit:disabled {
    cursor: progress;
    opacity: 0.85;
  }

  .contact-form__submit.is-success {
    background: var(--ts-secondary);
    box-shadow: 0 6px 22px rgba(var(--ts-secondary-rgb), 0.5);
  }

  .contact-form__submit.is-error {
    background: #ef4444;
    box-shadow: 0 6px 22px rgba(239, 68, 68, 0.5);
  }

  .contact-form__spinner {
    width: 14px;
    height: 14px;
    border-radius: 50%;
    border: 2px solid rgba(255, 255, 255, 0.35);
    border-top-color: #fff;
    animation: contact-spin 0.7s linear infinite;
  }

  @keyframes contact-spin {
    to {
      transform: rotate(360deg);
    }
  }

  .contact-form__status {
    margin: 14px 0 0;
    padding: 10px 14px;
    border-radius: 6px;
    background: rgba(var(--ts-secondary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-secondary-rgb), 0.3);
    color: var(--ts-secondary);
    font-size: 13px;
    line-height: 1.5;
    text-align: center;
  }

  .contact-form__status.is-error {
    background: rgba(239, 68, 68, 0.1);
    border-color: rgba(239, 68, 68, 0.35);
    color: #fca5a5;
  }

  .contact-form__badge {
    position: absolute;
    right: 20px;
    bottom: 20px;
    z-index: 6;
    display: inline-flex;
    align-items: center;
    gap: 9px;
    padding: 9px 16px 9px 11px;
    border-radius: 999px;
    background: var(--ts-secondary);
    border: 1px solid rgba(255, 255, 255, 0.2);
    box-shadow: 0 10px 30px rgba(var(--ts-secondary-rgb), 0.45);
    color: #fff;
    font-family: var(--ts-font-accent);
    font-size: 12px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    animation: contact-badge-in 300ms cubic-bezier(0.2, 0.9, 0.3, 1.2) both;
  }

  .contact-form__badge-icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 18px;
    height: 18px;
    border-radius: 50%;
    background: rgba(255, 255, 255, 0.22);
    flex-shrink: 0;
  }

  :global(.contact-form__badge-check) {
    width: 11px;
    height: 11px;
  }

  @keyframes contact-badge-in {
    from {
      opacity: 0;
      transform: translateY(10px) scale(0.94);
    }
    to {
      opacity: 1;
      transform: translateY(0) scale(1);
    }
  }

  :global(.contact-form__submit-icon) {
    width: 14px;
    height: 14px;
  }

  .contact-form__note {
    margin-top: 20px;
    padding: 12px 16px;
    border-radius: 6px;
    background: rgba(var(--ts-primary-rgb), 0.06);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.15);
    font-size: 12px;
    color: var(--ts-text-muted);
    line-height: 1.5;
    display: flex;
    align-items: center;
    gap: 10px;
  }

  :global(.contact-form__note-icon) {
    width: 14px;
    height: 14px;
    color: var(--ts-primary-light);
    flex-shrink: 0;
  }

  .contact-channels__title {
    font-size: 22px;
    font-weight: 700;
    color: var(--ts-text);
    margin: 0 0 20px;
  }

  .contact-channels__list {
    display: grid;
    gap: 10px;
  }

  :global(.contact-channel) {
    display: flex;
    align-items: center;
    gap: 14px;
    padding: 18px;
    border-radius: 8px;
    text-decoration: none;
    transition:
      transform 200ms ease,
      border-color 200ms ease,
      box-shadow 200ms ease;
  }

  :global(.contact-channel:hover) {
    transform: translateX(3px);
    border-color: rgba(var(--ts-primary-rgb), 0.4);
    box-shadow: 0 6px 20px rgba(var(--ts-primary-rgb), 0.15);
  }

  .contact-channel__icon {
    width: 40px;
    height: 40px;
    border-radius: 6px;
    flex-shrink: 0;
    background: rgba(var(--ts-primary-rgb), 0.14);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--ts-primary-light);
  }

  :global(.contact-channel__icon-svg) {
    width: 18px;
    height: 18px;
    color: var(--ts-primary-light);
  }

  .contact-channel__body {
    flex: 1;
  }

  .contact-channel__label {
    font-family: var(--ts-font-accent);
    font-size: 10px;
    color: var(--ts-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.14em;
    font-weight: 700;
    margin-bottom: 3px;
  }

  .contact-channel__value {
    font-size: 14px;
    color: var(--ts-text);
    font-weight: 600;
  }

  :global(.contact-channel__chev) {
    width: 14px;
    height: 14px;
    color: var(--ts-text-muted);
    transition: transform 200ms ease;
  }

  :global(.contact-channel:hover .contact-channel__chev) {
    transform: translateX(2px);
    color: var(--ts-primary-light);
  }

  @media (max-width: 960px) {
    .contact-hero {
      padding: 80px 0 40px;
    }
    .contact-hero__inner,
    .contact-body__inner {
      padding: 0 20px;
    }
    .contact-body__grid {
      grid-template-columns: 1fr;
      gap: 32px;
    }
    .contact-form {
      padding: 24px;
    }
    .contact-form__row {
      grid-template-columns: 1fr;
    }
    .contact-form__badge {
      right: 16px;
      bottom: 16px;
    }
  }
</style>
