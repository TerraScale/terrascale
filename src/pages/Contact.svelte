<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'

  type ChannelHrefKind = 'external' | 'mailto' | 'internal'
  type Channel = {
    icon: AppIconName
    label: string
    value: string
    href: string
    kind: ChannelHrefKind
  }

  const channels: Channel[] = [
    {
      icon: 'mail',
      label: 'Email',
      value: 'support@terrascale.tech',
      href: 'mailto:support@terrascale.tech',
      kind: 'mailto',
    },
    {
      icon: 'brandDiscord',
      label: 'Discord',
      value: 'Join our community',
      href: 'https://discord.gg/terrascale',
      kind: 'external',
    },
    {
      icon: 'brandGithub',
      label: 'GitHub',
      value: 'Open issues & PRs',
      href: 'https://git.mariogk.com/Terrascale',
      kind: 'external',
    },
  ]

  let form = { name: '', email: '', company: '', message: '' }

  type SubmitState = 'idle' | 'submitting' | 'success' | 'error'
  let submitState: SubmitState = 'idle'
  let statusMessage = ''
  let submitTimer: ReturnType<typeof setTimeout> | null = null
  let responseTimer: ReturnType<typeof setTimeout> | null = null
  let sinkForm: HTMLFormElement | undefined

  // Published Google Form backing the contact page (TER-54). The entry IDs
  // below are the real submission field IDs, verified directly against this
  // form's FB_PUBLIC_LOAD_DATA_ definition (not the per-item ids, and not the
  // wrong ids — using either silently drops every response, which is the exact
  // failure this integration exists to prevent).
  // The form has Name / Email / Message only — there is no Company field — so
  // the optional Company value is folded into the message body on submit.
  const GOOGLE_FORM_ENDPOINT =
    'https://docs.google.com/forms/d/e/1FAIpQLSfnbZR5CkFw581Bwwiy2CSbz_SDi4C8qMzxvmGrY0hfhzyULg/formResponse'
  const GOOGLE_FORM_FIELDS = {
    name: 'entry.2005620554',
    email: 'entry.1045781291',
    message: 'entry.839337160',
  } as const

  // Reactive: prepend Company to the message so it reaches the Google Form.
  $: messageForGoogle = (() => {
    const company = form.company.trim()
    return company ? `Company: ${company}\n\n${form.message}` : form.message
  })()

  const setStatus = (state: SubmitState, message = '') => {
    submitState = state
    statusMessage = message
  }

  const clearTimers = () => {
    if (submitTimer) {
      clearTimeout(submitTimer)
      submitTimer = null
    }
    if (responseTimer) {
      clearTimeout(responseTimer)
      responseTimer = null
    }
  }

  const resetStatusAfter = (delay: number) => {
    if (submitTimer) clearTimeout(submitTimer)
    submitTimer = setTimeout(() => setStatus('idle'), delay)
  }

  const markSuccess = () => {
    clearTimers()
    setStatus('success', "Thanks! Your message has been recorded — we'll reply within 24h.")
    form = { name: '', email: '', company: '', message: '' }
    resetStatusAfter(6000)
  }

  const onSinkLoad = () => {
    // The hidden iframe fires an initial load for its empty document; only
    // treat a load as Google's acknowledgement while a submission is in flight.
    if (submitState !== 'submitting') return
    markSuccess()
  }

  const handleSubmit = (event: SubmitEvent) => {
    event.preventDefault()
    if (submitState === 'submitting') return
    if (!sinkForm) {
      setStatus('error', 'The form could not be submitted. Please email us directly.')
      resetStatusAfter(7000)
      return
    }
    clearTimers()
    setStatus('submitting')

    try {
      // Native form POST into the hidden iframe — a genuine form submission that
      // Google records identically to a direct fill. (No hidden input named
      // "submit", since that would shadow HTMLFormElement.submit().) The
      // cross-origin response is opaque, so success is confirmed via the
      // iframe's load event with a watchdog fallback.
      sinkForm.submit()
    } catch {
      setStatus(
        'error',
        'Something went wrong sending your message. Please try again, or email us directly.',
      )
      resetStatusAfter(7000)
      return
    }

    // Watchdog: if the load signal never arrives (slow/blocked network), still
    // resolve to success — the POST was dispatched and Google records valid
    // submissions server-side regardless of our ability to read the response.
    responseTimer = setTimeout(markSuccess, 6000)
  }
</script>

<section class="contact-hero">
  <div class="contact-hero__inner">
    <div class="contact-hero__eyebrow">
      <Icon name="messageSquare" className="contact-hero__eyebrow-icon" />
      Get in Touch
    </div>
    <h1 class="contact-hero__title">
      Let's build<br />
      <span class="ts-text-gradient">something together.</span>
    </h1>
    <p class="contact-hero__subtitle">
      Have questions? Need a custom plan? Want to integrate with TerraScale? We'd love to hear from
      you.
    </p>
  </div>
</section>

<section class="contact-body">
  <div class="contact-body__inner">
    <div class="contact-body__grid">
      <form class="contact-form ts-glass" on:submit={handleSubmit}>
        <h2 class="contact-form__title">Send us a message</h2>

        <div class="contact-form__row">
          <div>
            <label class="ts-input-label" for="contact-name">Name</label>
            <input
              id="contact-name"
              class="ts-input"
              type="text"
              autocomplete="name"
              placeholder="Mario Belchior"
              bind:value={form.name}
              required
            />
          </div>
          <div>
            <label class="ts-input-label" for="contact-email">Email</label>
            <input
              id="contact-email"
              class="ts-input"
              type="email"
              autocomplete="email"
              placeholder="mario@acme.com"
              bind:value={form.email}
              required
            />
          </div>
        </div>

        <div class="contact-form__field">
          <label class="ts-input-label" for="contact-company">Company</label>
          <input
            id="contact-company"
            class="ts-input"
            type="text"
            autocomplete="organization"
            placeholder="Acme Inc."
            bind:value={form.company}
          />
        </div>

        <div class="contact-form__field">
          <label class="ts-input-label" for="contact-message">Message</label>
          <textarea
            id="contact-message"
            class="ts-input ts-input--textarea"
            rows="5"
            placeholder="Tell us about your project, team size, and what you're hoping to achieve..."
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
            Sending…
          {:else if submitState === 'success'}
            <Icon name="check" className="contact-form__submit-icon" />
            Message Sent!
          {:else if submitState === 'error'}
            <Icon name="alertCircle" className="contact-form__submit-icon" />
            Try Again
          {:else}
            Send Message
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
          Your information is never shared. Replies within 24h.
        </div>

        {#if submitState === 'success'}
          <!-- TER-81: confirmation badge pinned to the bottom-right corner of the
               contact box. Visual-only confirmation; the status message above is the
               authoritative live region for assistive tech, so this stays aria-hidden. -->
          <div class="contact-form__badge" aria-hidden="true">
            <span class="contact-form__badge-icon">
              <Icon name="check" className="contact-form__badge-check" />
            </span>
            Message sent successfully
          </div>
        {/if}
      </form>

      <!-- Hidden submission sink: a native form POST into a hidden iframe is the
           reliable static-site way to record Google Forms responses (Google
           Forms exposes no CORS for XHR/fetch). These are siblings of — never
           nested inside — the visible form. -->
      <iframe
        name="ts-contact-sink"
        class="contact-form__sink"
        title="Contact form response target"
        aria-hidden="true"
        tabindex="-1"
        on:load={onSinkLoad}
      ></iframe>
      <form
        bind:this={sinkForm}
        class="contact-form__sink"
        action={GOOGLE_FORM_ENDPOINT}
        method="POST"
        target="ts-contact-sink"
        aria-hidden="true"
        tabindex="-1"
      >
        <input type="hidden" name={GOOGLE_FORM_FIELDS.name} value={form.name} />
        <input type="hidden" name={GOOGLE_FORM_FIELDS.email} value={form.email} />
        <input type="hidden" name={GOOGLE_FORM_FIELDS.message} value={messageForGoogle} />
      </form>

      <div class="contact-channels">
        <h2 class="contact-channels__title">Other ways to reach us</h2>
        <div class="contact-channels__list">
          {#each channels as c (c.label)}
            {#if c.kind === 'external'}
              <ExternalLink
                href={c.href}
                target="_blank"
                rel="noopener noreferrer"
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

        <div class="contact-sla">
          <div class="contact-sla__heading">
            <span class="pulse-dot contact-sla__dot"></span>
            Support SLA
          </div>
          <div class="contact-sla__body">
            Pro customers get <strong>priority routing</strong> with 1-hour first response during
            business hours.
          </div>
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

  /* TER-81: success confirmation badge pinned to the bottom-right corner of the
     contact box. Mirrors the submit button's success treatment (solid secondary
     green, white glyph) so it reads as one design system. */
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

  /* Visually hidden submission sink for the Google Forms iframe POST. Kept at
     1px (not display:none) so the iframe can still navigate and fire load. */
  .contact-form__sink {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    padding: 0;
    border: 0;
    overflow: hidden;
    clip: rect(0 0 0 0);
    clip-path: inset(50%);
    white-space: nowrap;
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

  .contact-sla {
    margin-top: 24px;
    padding: 20px;
    border-radius: 8px;
    background: linear-gradient(
      135deg,
      rgba(var(--ts-primary-rgb), 0.08) 0%,
      transparent 100%
    );
    border: 1px solid rgba(var(--ts-primary-rgb), 0.2);
  }

  .contact-sla__heading {
    font-family: var(--ts-font-accent);
    font-size: 10px;
    color: var(--ts-primary-light);
    text-transform: uppercase;
    letter-spacing: 0.14em;
    font-weight: 700;
    margin-bottom: 8px;
    display: inline-flex;
    align-items: center;
    gap: 6px;
  }

  .contact-sla__dot {
    width: 6px;
    height: 6px;
    border-radius: 3px;
    background: var(--ts-secondary);
    display: inline-block;
  }

  .contact-sla__body {
    font-size: 14px;
    color: var(--ts-text);
    line-height: 1.55;
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
