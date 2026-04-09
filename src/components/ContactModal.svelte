<script lang="ts">
  import { createEventDispatcher } from 'svelte'
  import { fade, scale } from 'svelte/transition'
  import Icon from '@/components/Icon.svelte'

  export let open = false

  const dispatch = createEventDispatcher<{ close: void }>()

  let formData = {
    name: '',
    email: '',
    company: '',
    message: '',
  }

  const close = () => {
    dispatch('close')
  }

  const handleSubmit = (event: SubmitEvent) => {
    event.preventDefault()

    const googleFormUrl =
      'https://docs.google.com/forms/d/e/1FAIpQLSdXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX/formResponse'
    const params = new URLSearchParams({
      'entry.1234567': formData.name,
      'entry.2345678': formData.email,
      'entry.3456789': formData.company,
      'entry.4567890': formData.message,
    })

    window.open(`${googleFormUrl}?${params.toString()}`, '_blank', 'noopener,noreferrer')
    close()
  }

  const openCalendly = () => {
    window.open(
      'https://calendly.com/terrascale/enterprise-consultation',
      '_blank',
      'noopener,noreferrer',
    )
  }

  const handleBackdropClick = (event: MouseEvent) => {
    if (event.target === event.currentTarget) {
      close()
    }
  }

  const handleBackdropKeydown = (event: KeyboardEvent) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      close()
    }
  }

  const handleKeydown = (event: KeyboardEvent) => {
    if (open && event.key === 'Escape') {
      close()
    }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

{#if open}
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4 backdrop-blur-sm"
    role="button"
    tabindex="0"
    aria-label="Close modal backdrop"
    on:click={handleBackdropClick}
    on:keydown={handleBackdropKeydown}
    transition:fade={{ duration: 150 }}
  >
    <div
      class="ts-glass-frosted ts-rounded-xl max-h-[90vh] w-full max-w-md overflow-y-auto border border-[#1A1A24] bg-[#080810]"
      role="dialog"
      aria-modal="true"
      aria-labelledby="contact-modal-title"
      transition:scale={{ duration: 180, start: 0.96 }}
    >
      <div class="p-6">
        <div class="mb-6 flex items-center justify-between">
          <h2 id="contact-modal-title" class="text-2xl font-bold text-[#F8FAFC]">
            Contact Enterprise Sales
          </h2>
          <button
            type="button"
            class="ts-focus-glow ts-rounded-lg p-2 text-[#94A3B8] transition-colors hover:bg-[#0C0C10] hover:text-[#F8FAFC]"
            aria-label="Close"
            on:click={close}
          >
            <Icon name="x" className="h-5 w-5" />
          </button>
        </div>

        <div class="space-y-6">
          <div class="ts-rounded-lg border border-[#1A1A24] bg-gradient-to-r from-[#2563EB]/10 via-[#8B5CF6]/10 to-[#0891B2]/10 p-4 text-center">
            <p class="text-sm text-[#94A3B8]">
              Get personalized pricing and dedicated support for your enterprise needs
            </p>
          </div>

          <form class="space-y-4" on:submit={handleSubmit}>
            <div>
              <label class="mb-2 block text-sm font-medium text-[#F8FAFC]" for="modal-name">
                Full Name *
              </label>
              <input
                id="modal-name"
                class="ts-input w-full"
                type="text"
                required
                bind:value={formData.name}
              />
            </div>

            <div>
              <label class="mb-2 block text-sm font-medium text-[#F8FAFC]" for="modal-email">
                Business Email *
              </label>
              <input
                id="modal-email"
                class="ts-input w-full"
                type="email"
                required
                bind:value={formData.email}
              />
            </div>

            <div>
              <label class="mb-2 block text-sm font-medium text-[#F8FAFC]" for="modal-company">
                Company Name *
              </label>
              <input
                id="modal-company"
                class="ts-input w-full"
                type="text"
                required
                bind:value={formData.company}
              />
            </div>

            <div>
              <label class="mb-2 block text-sm font-medium text-[#F8FAFC]" for="modal-message">
                Tell us about your needs
              </label>
              <textarea
                id="modal-message"
                class="ts-input w-full resize-none"
                rows="3"
                placeholder="Describe your project requirements, expected scale, and any specific needs..."
                bind:value={formData.message}
              ></textarea>
            </div>

            <div class="flex flex-col gap-3 sm:flex-row">
              <button
                type="submit"
                class="ts-btn-primary ts-rounded-md flex flex-1 items-center justify-center gap-2 font-medium"
              >
                <Icon name="send" className="h-4 w-4" />
                <span>Send Message</span>
              </button>

              <button
                type="button"
                class="ts-btn-glass ts-rounded-md flex flex-1 items-center justify-center gap-2 font-medium"
                on:click={openCalendly}
              >
                <Icon name="calendar" className="h-4 w-4" />
                <span>Schedule Call</span>
              </button>
            </div>
          </form>

          <div class="text-center text-sm text-[#94A3B8]">
            <p>Our team typically responds within 24 hours</p>
          </div>
        </div>
      </div>
    </div>
  </div>
{/if}
