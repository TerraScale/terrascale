<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ScrollReveal from '@/components/ScrollReveal.svelte'
</script>

<svelte:head>
  <title>TerraScale Security</title>
  <meta
    name="description"
    content="Overview of TerraScale's security posture, infrastructure protections, and compliance direction."
  />
</svelte:head>

<section class="bg-[#050508] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="mx-auto max-w-4xl text-center">
      <ScrollReveal>
        <div class="mb-4 inline-flex items-center gap-2 bg-[#0891B2]/10 px-3 py-1 text-xs font-bold uppercase tracking-[0.18em] text-[#67E8F9] ts-rounded-sm">
          <Icon name="shieldCheck" className="h-4 w-4" />
          Security
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.05}>
        <h1 class="mb-6 text-4xl font-bold tracking-tight text-[#F8FAFC] md:text-6xl">
          Security At TerraScale
        </h1>
      </ScrollReveal>

      <ScrollReveal delay={0.1}>
        <p class="text-xl leading-relaxed text-[#94A3B8] md:text-2xl">
          TerraScale is built around secure defaults, operational visibility, and enterprise-ready
          controls. A full public security center is in progress.
        </p>
      </ScrollReveal>
    </div>
  </div>
</section>

<section class="bg-[#0A0A0E] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <ScrollReveal>
      <div class="mx-auto max-w-3xl p-8 ts-glass-frosted ts-rounded-lg">
        <h2 class="mb-4 text-2xl font-bold text-[#F8FAFC] md:text-3xl">
          What this page covers today
        </h2>
        <div class="space-y-4 text-[#94A3B8]">
          <p>Current customer-facing security documentation is available on request.</p>
          <p>
            For security questionnaires, compliance details, or reporting a vulnerability, contact
            the TerraScale team directly.
          </p>
        </div>

        <div class="mt-8 flex flex-col gap-4 sm:flex-row">
          <RouteLink
            href="/contact"
            class="ts-btn-primary ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold shadow-offset-blue ts-pressable"
          >
            <Icon name="messageSquare" className="mr-2 h-4 w-4" />
            <span>Contact The Team</span>
          </RouteLink>

          <RouteLink
            href="/contact"
            class="ts-btn-glass ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold shadow-offset ts-pressable"
          >
            <Icon name="mail" className="mr-2 h-4 w-4" />
            <span>Contact Security Team</span>
          </RouteLink>
        </div>
      </div>
    </ScrollReveal>
  </div>
</section>
