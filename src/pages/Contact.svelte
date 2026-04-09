<script lang="ts">
  import { fade, scale } from 'svelte/transition'
  import Icon from '@/components/Icon.svelte'
  import ScrollReveal from '@/components/ScrollReveal.svelte'
  import type { AppIconName } from '@/lib/icons'

  interface ContactFormData {
    name: string
    email: string
    message: string
  }

  interface Status {
    type: 'success' | 'error' | ''
    message: string
  }

  let formData: ContactFormData = {
    name: '',
    email: '',
    message: '',
  }

  let status: Status = { type: '', message: '' }
  let isSubmitting = false

  const contactInfo: Array<{
    icon: AppIconName
    label: string
    value: string
    href: string | null
  }> = [
    {
      icon: 'mail',
      label: 'Support',
      value: 'Use the contact form below',
      href: '#contact-form',
    },
    {
      icon: 'brandDiscord',
      label: 'Discord',
      value: 'Join our community',
      href: 'https://discord.gg/terrascale',
    },
    {
      icon: 'clock',
      label: 'Business Hours',
      value: 'Mon-Fri: 9am - 6pm PST',
      href: null,
    },
  ]

  const updateField = (field: keyof ContactFormData, value: string) => {
    formData = { ...formData, [field]: value }

    if (status.message) {
      status = { type: '', message: '' }
    }
  }

  const handleSubmit = async (event: SubmitEvent) => {
    event.preventDefault()

    if (!formData.name.trim() || !formData.email.trim() || !formData.message.trim()) {
      status = {
        type: 'error',
        message: 'Please fill in all fields.',
      }
      return
    }

    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

    if (!emailRegex.test(formData.email)) {
      status = {
        type: 'error',
        message: 'Please enter a valid email address.',
      }
      return
    }

    isSubmitting = true

    try {
      await new Promise((resolve) => setTimeout(resolve, 1000))

      status = {
        type: 'success',
        message: "Message sent! We'll get back to you soon.",
      }

      formData = {
        name: '',
        email: '',
        message: '',
      }
    } catch {
      status = {
        type: 'error',
        message: 'Something went wrong. Please try again later.',
      }
    } finally {
      isSubmitting = false
    }
  }
</script>

<section class="bg-[#050508] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="mx-auto max-w-4xl text-center">
      <ScrollReveal>
        <h1 class="mb-6 text-4xl font-bold tracking-tight text-[#F8FAFC] md:text-6xl">
          Contact Us
        </h1>
      </ScrollReveal>
      <ScrollReveal delay={0.1}>
        <p class="text-xl leading-relaxed text-[#94A3B8] md:text-2xl">
          We'd love to hear from you! Fill out the form or reach us through the info below.
        </p>
      </ScrollReveal>
    </div>
  </div>
</section>

<section class="bg-[#0A0A0E] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="grid grid-cols-1 gap-16 lg:grid-cols-2">
      <ScrollReveal direction="left">
        <div id="contact-form" class="hover-lift p-8 ts-glass ts-rounded-lg">
          <h2 class="mb-6 text-3xl font-bold text-[#F8FAFC]">
            Send us a message
          </h2>

          <form class="space-y-6" novalidate on:submit={handleSubmit}>
            <div>
              <label class="mb-2 block text-sm font-bold text-[#F8FAFC]" for="contact-name">
                Name *
              </label>
              <input
                id="contact-name"
                class="ts-input w-full shadow-offset"
                type="text"
                placeholder="Your full name"
                value={formData.name}
                required
                on:input={(event) => updateField('name', (event.currentTarget as HTMLInputElement).value)}
              />
            </div>

            <div>
              <label class="mb-2 block text-sm font-bold text-[#F8FAFC]" for="contact-email">
                Email *
              </label>
              <input
                id="contact-email"
                class="ts-input w-full shadow-offset"
                type="email"
                placeholder="your work email"
                value={formData.email}
                required
                on:input={(event) => updateField('email', (event.currentTarget as HTMLInputElement).value)}
              />
            </div>

            <div>
              <label class="mb-2 block text-sm font-bold text-[#F8FAFC]" for="contact-message">
                Message *
              </label>
              <textarea
                id="contact-message"
                class="ts-input w-full resize-none shadow-offset"
                rows="6"
                placeholder="Tell us about your project or question..."
                required
                on:input={(event) => updateField('message', (event.currentTarget as HTMLTextAreaElement).value)}
              >{formData.message}</textarea>
            </div>

            {#if status.message}
              <div
                class={`flex items-center space-x-2 border p-4 text-sm ts-rounded-sm ${
                  status.type === 'success'
                    ? 'border-[#10B981] bg-[#10B981]/10 text-[#10B981]'
                    : 'border-[#F43F5E] bg-[#F43F5E]/10 text-[#F43F5E]'
                }`}
                transition:fade={{ duration: 180 }}
              >
                {#if status.type === 'success'}
                  <div transition:scale={{ duration: 150, start: 0.8 }}>
                    <Icon name="checkCircle" className="h-5 w-5" />
                  </div>
                {:else}
                  <Icon name="alertCircle" className="h-5 w-5" />
                {/if}
                <span>{status.message}</span>
              </div>
            {/if}

            <button
              type="submit"
              class="ts-btn-primary ts-rounded-md inline-flex w-full items-center justify-center px-6 py-3 font-bold shadow-offset-blue hover-lift disabled:cursor-not-allowed disabled:opacity-50"
              disabled={isSubmitting}
            >
              {#if isSubmitting}
                <div class="mr-2 h-4 w-4 animate-spin rounded-full border-2 border-[#050508] border-t-transparent"></div>
                <span>Sending...</span>
              {:else}
                <Icon name="send" className="mr-2 h-4 w-4" />
                <span>Send Message</span>
              {/if}
            </button>
          </form>
        </div>
      </ScrollReveal>

      <ScrollReveal direction="right">
        <div>
          <h2 class="mb-6 text-3xl font-bold text-[#F8FAFC]">
            Get in touch
          </h2>
          <p class="mb-8 leading-relaxed text-[#94A3B8]">
            Have questions about our platform? Need help with your account? Our team is here to
            help you succeed.
          </p>

          <div class="space-y-6">
            {#each contactInfo as info, index (info.label)}
              <ScrollReveal delay={index * 0.08}>
                <div class="flex items-start space-x-4">
                  <div class="flex h-10 w-10 shrink-0 items-center justify-center border-2 border-[#1A1A24] bg-[#050508] transition-transform duration-200 hover:rotate-[2deg] hover:scale-110 hover:border-[#2563EB] ts-rounded-sm">
                    <Icon name={info.icon} className="h-5 w-5 text-[#2563EB]" />
                  </div>
                  <div>
                    <h3 class="mb-1 font-bold text-[#F8FAFC]">{info.label}</h3>
                    {#if info.href}
                      <a
                        href={info.href}
                        class="text-sm text-[#94A3B8] transition-colors duration-200 hover:text-[#2563EB]"
                        target={info.href.startsWith('http') ? '_blank' : undefined}
                        rel={info.href.startsWith('http') ? 'noopener noreferrer' : undefined}
                      >
                        {info.value}
                      </a>
                    {:else}
                      <span class="text-sm text-[#94A3B8]">{info.value}</span>
                    {/if}
                  </div>
                </div>
              </ScrollReveal>
            {/each}
          </div>

          <div class="hover-lift mt-8 p-6 transition-colors duration-200 hover:border-[#5865F2] ts-glass-frosted ts-rounded-lg">
            <h3 class="mb-2 flex items-center font-bold text-[#F8FAFC]">
              <Icon name="brandDiscord" className="mr-2 h-5 w-5 text-[#5865F2]" />
              Join our Discord community
            </h3>
            <p class="mb-4 text-sm text-[#94A3B8]">
              Connect with other developers, get help, and stay updated with the latest news.
            </p>
            <a
              href="https://discord.gg/terrascale"
              target="_blank"
              rel="noopener noreferrer"
              class="ts-rounded-md inline-flex items-center bg-[#5865F2] px-4 py-2 text-sm font-bold text-[#F8FAFC] shadow-offset transition-colors duration-200 hover:bg-[#4752C4]"
            >
              <Icon name="brandDiscord" className="mr-2 h-4 w-4" />
              <span>Join Discord</span>
            </a>
          </div>
        </div>
      </ScrollReveal>
    </div>
  </div>
</section>
