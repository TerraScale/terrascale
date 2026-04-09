<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ScrollReveal from '@/components/ScrollReveal.svelte'
</script>

<section class="flex min-h-screen items-center bg-gradient-to-b from-[#050508] to-[#0A0A0E] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="mx-auto max-w-2xl text-center">
      <ScrollReveal>
        <div class="mb-8">
          <div class="animate-bob">
            <svg
              class="mx-auto h-64 w-64 text-[#475569]/40"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="1.5"
                d="M9.172 16.172a4 4 0 015.656 0M9 12h6m-3-4v.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
              ></path>
            </svg>
          </div>
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.1}>
        <div class="mb-6">
          <h1 class="mb-4 bg-gradient-to-r from-[#2563EB] to-[#10B981] bg-clip-text text-8xl font-bold text-transparent md:text-9xl">
            404
          </h1>
          <h2 class="text-3xl font-bold text-[#F8FAFC] md:text-4xl">Not Found</h2>
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.2}>
        <p class="mb-2 text-xl text-[#94A3B8]">It's gone :(</p>
        <p class="mb-8 text-lg text-[#94A3B8]">
          The page you're looking for seems to have vanished into the digital void. Don't worry,
          even the best explorers sometimes take a wrong turn!
        </p>
      </ScrollReveal>

      <ScrollReveal delay={0.3}>
        <div class="flex flex-col items-center justify-center gap-4 sm:flex-row">
          <RouteLink
            href="/"
            class="ts-btn-primary ts-rounded-md inline-flex items-center px-6 py-3 font-semibold ts-pressable"
          >
            <Icon name="home" className="mr-2 h-4 w-4" />
            <span>Back to Home</span>
          </RouteLink>

          <RouteLink
            href="/contact"
            class="ts-btn-glass ts-rounded-md inline-flex items-center px-6 py-3 font-semibold ts-pressable"
          >
            <Icon name="search" className="mr-2 h-4 w-4" />
            <span>Contact Support</span>
          </RouteLink>
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.4}>
        <div class="mt-12 border-t border-[#1A1A24] pt-8">
          <p class="mb-4 text-[#94A3B8]">Or try one of these popular pages:</p>
          <div class="flex flex-wrap justify-center gap-6 text-sm">
            <RouteLink href="/about" class="text-[#2563EB] transition-colors duration-200 hover:text-[#60A5FA]">
              About Us
            </RouteLink>
            <RouteLink href="/pricing" class="text-[#2563EB] transition-colors duration-200 hover:text-[#60A5FA]">
              Pricing
            </RouteLink>
            <RouteLink href="/contact" class="text-[#2563EB] transition-colors duration-200 hover:text-[#60A5FA]">
              Contact
            </RouteLink>
          </div>
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.5}>
        <div class="mt-8">
          <div class="animate-slide-sideways inline-flex items-center space-x-2 text-[#94A3B8]">
            <Icon name="arrowLeft" className="h-4 w-4" />
            <span class="text-sm">This way back to safety</span>
          </div>
        </div>
      </ScrollReveal>
    </div>
  </div>
</section>
