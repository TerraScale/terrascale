<script lang="ts">
  import AnimatedCounter from '@/components/AnimatedCounter.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ScrollReveal from '@/components/ScrollReveal.svelte'
  import type { AppIconName } from '@/lib/icons'

  const values: Array<{ icon: AppIconName; title: string; description: string }> = [
    {
      icon: 'target',
      title: 'Transparency',
      description: 'We believe in open communication and honest relationships with our users.',
    },
    {
      icon: 'users',
      title: 'Customer-Centricity',
      description: "Every decision we make is guided by what's best for our customers.",
    },
    {
      icon: 'shield',
      title: 'Innovation',
      description: 'We continuously push boundaries to deliver cutting-edge solutions.',
    },
    {
      icon: 'award',
      title: 'Continuous Improvement',
      description: 'We never stop learning, growing, and improving our platform.',
    },
  ]

  const stats = [
    { value: '10000+', label: 'Active Developers' },
    { value: '99.99%', label: 'Uptime SLA' },
    { value: '24/7', label: 'Support', noCounter: true },
    { value: '20+', label: 'Global Regions' },
  ]
</script>

<section class="bg-[#050508] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="mx-auto max-w-4xl text-center">
      <ScrollReveal>
        <h1 class="mb-6 text-4xl font-bold tracking-tight text-[#F8FAFC] md:text-6xl">
          About TerraScale
        </h1>
      </ScrollReveal>
      <ScrollReveal delay={0.1}>
        <p class="text-xl leading-relaxed text-[#94A3B8] md:text-2xl">
          Our mission is to empower businesses with scalable, secure, and innovative database
          solutions. We believe in transparency, customer-centricity, and continuous improvement.
        </p>
      </ScrollReveal>
    </div>
  </div>
</section>

<section class="bg-[#0A0A0E] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="mb-24 grid grid-cols-1 items-center gap-16 lg:grid-cols-2">
      <ScrollReveal direction="left">
        <div>
          <h2 class="mb-6 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
            Our Mission
          </h2>
          <p class="mb-6 text-lg leading-relaxed text-[#94A3B8]">
            At TerraScale, we're on a mission to democratize enterprise-grade database
            infrastructure. We believe that every business, regardless of size, should have access
            to powerful, scalable, and secure database solutions.
          </p>
          <p class="mb-6 text-lg leading-relaxed text-[#94A3B8]">
            Founded on principles of innovation and reliability, we've built a platform that grows
            with your business, ensuring you're never held back by technical limitations or database
            performance issues.
          </p>
          <div class="ts-glass-primary ts-rounded-lg border-l-4 border-[#2563EB] p-6">
            <p class="text-sm font-medium italic text-[#F8FAFC]">
              "Database technology should empower, not complicate. That's the driving force behind
              everything we build at TerraScale."
            </p>
          </div>
        </div>
      </ScrollReveal>

      <ScrollReveal direction="right">
        <div class="relative">
          <div class="shadow-offset hover-lift p-8 ts-glass-frosted ts-rounded-lg">
            <div class="flex h-64 items-center justify-center border border-[#1A1A24] bg-[#050508] ts-rounded-sm">
              <div class="text-center">
                <div class="mx-auto mb-4 flex h-16 w-16 animate-pulse-soft items-center justify-center bg-[#2563EB] ts-hover-glow ts-rounded-sm">
                  <Icon name="target" className="h-8 w-8 text-[#050508]" />
                </div>
                <p class="font-medium text-[#94A3B8]">Our Vision</p>
              </div>
            </div>
          </div>
        </div>
      </ScrollReveal>
    </div>

    <ScrollReveal>
      <div class="mb-16 text-center">
        <h2 class="mb-4 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
          Our Core Values
        </h2>
        <p class="mx-auto max-w-2xl text-xl text-[#94A3B8]">
          The principles that guide every decision we make and every solution we build.
        </p>
      </div>
    </ScrollReveal>

    <div class="grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-4">
      {#each values as value, index (value.title)}
        <ScrollReveal delay={index * 0.08}>
          <div class="hover-lift h-full p-6 text-center transition-colors duration-200 hover:border-[#2563EB] ts-glass ts-rounded-lg">
            <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center border-2 border-[#1A1A24] bg-[#050508] ts-rounded-sm transition-transform duration-200 hover:rotate-[2deg] hover:scale-110 hover:border-[#2563EB]">
              <Icon name={value.icon} className="h-6 w-6 text-[#2563EB]" />
            </div>
            <h3 class="mb-3 text-xl font-bold text-[#F8FAFC]">{value.title}</h3>
            <p class="text-sm text-[#94A3B8]">{value.description}</p>
          </div>
        </ScrollReveal>
      {/each}
    </div>
  </div>
</section>

<section class="bg-[#050508] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <ScrollReveal>
      <div class="mb-16 text-center">
        <h2 class="mb-4 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
          Our Team
        </h2>
        <p class="mx-auto max-w-2xl text-xl text-[#94A3B8]">
          Meet the passionate individual building the future of scalable database technology.
        </p>
      </div>
    </ScrollReveal>

    <ScrollReveal>
      <div class="flex justify-center">
        <div class="hover-lift max-w-md p-8 transition-colors duration-200 hover:border-[#2563EB] ts-glass ts-rounded-lg">
          <div class="text-center">
            <div class="mx-auto mb-6 flex h-24 w-24 items-center justify-center bg-[#2563EB] text-2xl font-bold text-[#050508] ts-hover-glow ts-rounded-sm">
              MG
            </div>
            <h3 class="mb-2 text-2xl font-bold text-[#F8FAFC]">Mario Gabriell Karaziaki Belchior</h3>
            <p class="mb-4 text-sm font-bold text-[#2563EB]">CEO & Founder</p>
            <p class="mb-4 leading-relaxed text-[#94A3B8]">
              Building the future of scalable database technology with passion and vision. Mario
              combines deep technical expertise with strategic leadership to drive TerraScale's
              mission forward.
            </p>
            <RouteLink
              href="/contact"
              class="inline-flex items-center text-sm text-[#94A3B8] transition-colors duration-200 hover:text-[#2563EB]"
            >
              <Icon name="mail" className="mr-2 h-4 w-4" />
              <span>Contact TerraScale</span>
            </RouteLink>
          </div>
        </div>
      </div>
    </ScrollReveal>

    <ScrollReveal delay={0.2}>
      <div class="mt-16 text-center">
        <div class="p-8 transition-colors duration-200 hover:border-[#2563EB] ts-glass-frosted ts-rounded-lg">
          <h3 class="mb-4 text-2xl font-bold text-[#F8FAFC]">Join Our Team</h3>
          <p class="mx-auto mb-6 max-w-2xl text-[#94A3B8]">
            We're always looking for talented individuals who share our passion for building
            innovative database solutions. Join us in shaping the future of data infrastructure.
          </p>
          <div class="flex flex-col justify-center gap-4 sm:flex-row">
            <RouteLink
              href="/contact"
              class="ts-btn-primary ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold shadow-offset-blue ts-pressable"
            >
              <Icon name="messageSquare" className="mr-2 h-4 w-4" />
              <span>Get in Touch</span>
            </RouteLink>

            <a
              href="https://docs.terrascale.tech"
              target="_blank"
              rel="noopener noreferrer"
              class="ts-btn-glass ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold shadow-offset ts-pressable"
            >
              <Icon name="bookOpen" className="mr-2 h-4 w-4" />
              <span>Learn More</span>
            </a>
          </div>
        </div>
      </div>
    </ScrollReveal>
  </div>
</section>

<section class="relative overflow-hidden bg-[#050508] bg-grid-pattern py-24">
  <ScrollReveal>
    <div class="relative z-10 mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
      <div class="mb-12 text-center">
        <h2 class="mb-4 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
          TerraScale by Numbers
        </h2>
        <p class="text-xl text-[#94A3B8]">Trusted by developers and businesses worldwide</p>
      </div>

      <div class="grid grid-cols-1 gap-6 text-center md:grid-cols-4">
        {#each stats as stat, index (stat.label)}
          <ScrollReveal delay={index * 0.08}>
            <div class="ts-stats-card">
              <div class="mb-2 text-4xl font-bold ts-text-gradient md:text-5xl">
                {#if stat.noCounter}
                  {stat.value}
                {:else}
                  <AnimatedCounter value={stat.value} />
                {/if}
              </div>
              <div class="terminal-line text-sm text-[#94A3B8]">{stat.label}</div>
            </div>
          </ScrollReveal>
        {/each}
      </div>
    </div>
  </ScrollReveal>
</section>
