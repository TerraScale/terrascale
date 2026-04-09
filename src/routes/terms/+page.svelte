<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ScrollReveal from '@/components/ScrollReveal.svelte'
</script>

<svelte:head>
  <title>TerraScale Terms</title>
  <meta
    name="description"
    content="Temporary TerraScale terms page placeholder until the full legal documents are published."
  />
</svelte:head>

<section class="bg-[#050508] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="mx-auto max-w-4xl text-center">
      <ScrollReveal>
        <div class="mb-4 inline-flex items-center gap-2 bg-[#8B5CF6]/10 px-3 py-1 text-xs font-bold uppercase tracking-[0.18em] text-[#C4B5FD] ts-rounded-sm">
          <Icon name="bookOpen" className="h-4 w-4" />
          Terms
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.05}>
        <h1 class="mb-6 text-4xl font-bold tracking-tight text-[#F8FAFC] md:text-6xl">
          Terms And Legal Information
        </h1>
      </ScrollReveal>

      <ScrollReveal delay={0.1}>
        <p class="text-xl leading-relaxed text-[#94A3B8] md:text-2xl">
          TerraScale's full public legal pages are being prepared. If you need the current terms or
          contract details, contact us directly and we will provide the appropriate documents.
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
          Need the current terms now?
        </h2>
        <div class="space-y-4 text-[#94A3B8]">
          <p>We can share the latest commercial and legal material directly during onboarding.</p>
          <p>
            Reach out if you need procurement support, enterprise terms, or a copy of the current
            customer agreement.
          </p>
        </div>

        <div class="mt-8 flex flex-col gap-4 sm:flex-row">
          <RouteLink
            href="/contact"
            class="ts-btn-primary ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold shadow-offset-blue ts-pressable"
          >
            <Icon name="messageSquare" className="mr-2 h-4 w-4" />
            <span>Request Terms</span>
          </RouteLink>

          <RouteLink
            href="/contact"
            class="ts-btn-glass ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold shadow-offset ts-pressable"
          >
            <Icon name="mail" className="mr-2 h-4 w-4" />
            <span>Contact TerraScale</span>
          </RouteLink>
        </div>
      </div>
    </ScrollReveal>
  </div>
</section>
