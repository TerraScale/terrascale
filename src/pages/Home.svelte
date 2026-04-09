<script lang="ts">
  import AnimatedCounter from '@/components/AnimatedCounter.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ScrollReveal from '@/components/ScrollReveal.svelte'
  import type { AppIconName } from '@/lib/icons'

  const features: Array<{ icon: AppIconName; title: string; description: string }> = [
    {
      icon: 'zap',
      title: 'Lightning Fast',
      description: 'Blazing fast queries with global edge caching and optimized indexing.',
    },
    {
      icon: 'barChart3',
      title: 'Real-Time Analytics',
      description: 'Monitor your data and performance with real-time insights and dashboards.',
    },
    {
      icon: 'shield',
      title: 'Enterprise Security',
      description: 'SOC 2 compliant with end-to-end encryption and advanced access controls.',
    },
    {
      icon: 'globe',
      title: 'Global Scale',
      description: 'Deploy worldwide with automatic failover and geographic distribution.',
    },
  ]

  const testimonials = [
    {
      name: 'Sam Carter',
      role: 'CTO, ExampleCorp',
      content: 'TerraScale transformed our data infrastructure. The performance gains are incredible.',
      avatar: 'SC',
    },
    {
      name: 'Priya Patel',
      role: 'Founder, Startly',
      content: 'The developer experience is outstanding. We shipped faster than ever before.',
      avatar: 'PP',
    },
    {
      name: 'Liam Nguyen',
      role: 'Product Manager',
      content: 'Reliable, secure, and scales effortlessly. TerraScale is our backbone.',
      avatar: 'LN',
    },
  ]

  const stats = [
    { value: '99.99%', label: 'Uptime SLA' },
    { value: '10ms', label: 'Query Latency' },
    { value: '1M+', label: 'Queries/Second' },
    { value: '20+', label: 'Global Regions' },
  ]

  const codeSnippet = `import { TerraScale } from '@terrascale/sdk'

const db = new TerraScale({
  apiKey: process.env.TERRASCALE_API_KEY,
  region: 'us-west-2'
})

// Create a new document
await db.collection('users').create({
  name: 'John Doe',
  email: 'john@example.com'
})

// Query with real-time filters
const users = await db.collection('users')
  .where('status', '==', 'active')
  .orderBy('created_at')
  .limit(10)
  .get()`
</script>

<section class="relative flex min-h-screen items-center justify-center overflow-hidden bg-[#050508]">
  <div class="absolute inset-0 bg-grid-pattern opacity-40"></div>

  <div class="animate-float-slow absolute left-[15%] top-1/4 h-96 w-96 border border-[#1A1A24] ts-rounded-md opacity-20">
    <div class="absolute inset-0 bg-gradient-to-br from-[#2563EB]/5 to-transparent"></div>
  </div>

  <div class="animate-float-reverse absolute bottom-1/4 right-[15%] h-80 w-80 border border-[#1A1A24] ts-rounded-md opacity-20">
    <div class="absolute inset-0 bg-gradient-to-br from-[#10B981]/5 to-transparent"></div>
  </div>

  <div class="relative z-10 mx-auto max-w-[1440px] px-4 py-20 sm:px-6 lg:px-8">
    <div class="mx-auto max-w-4xl text-center">
      <ScrollReveal delay={0.1}>
        <div class="mb-8 inline-flex items-center px-4 py-2 text-sm font-medium text-[#F8FAFC] hover-lift ts-glass ts-rounded-sm">
          <span class="mr-3 h-2 w-2 animate-pulse ts-glow-secondary ts-rounded-sm bg-[#10B981]"></span>
          <Icon name="database" className="mr-2 h-4 w-4 text-[#2563EB]" />
          <span>Database as a Service Platform</span>
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.2}>
        <div class="-translate-y-4 mb-4 flex justify-center md:-translate-y-6">
          <img
            src="/logo-principal.svg"
            alt="TerraScale"
            class="h-20 w-auto sm:h-24 md:h-28 lg:h-32"
          />
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.3}>
        <p class="mx-auto mb-12 max-w-3xl text-xl leading-relaxed text-[#94A3B8] md:text-2xl">
          The most advanced database platform for modern applications. Deploy, scale, and manage
          your data with enterprise-grade security and performance.
        </p>
      </ScrollReveal>

      <ScrollReveal delay={0.4}>
        <div class="mb-16 flex flex-col items-center justify-center gap-4 sm:flex-row">
          <RouteLink
            href="/contact"
            class="ts-btn-primary ts-rounded-md inline-flex items-center px-8 py-4 text-lg font-bold shadow-offset-blue ts-pressable"
          >
            <span>Start Building Free</span>
            <Icon name="arrowRight" className="ml-2 h-5 w-5" />
          </RouteLink>

          <a
            href="https://docs.terrascale.tech"
            target="_blank"
            rel="noopener noreferrer"
            class="ts-btn-glass ts-rounded-md inline-flex items-center px-8 py-4 text-lg font-bold shadow-offset ts-pressable"
          >
            <Icon name="bookOpen" className="mr-2 h-5 w-5" />
            <span>View Documentation</span>
          </a>
        </div>
      </ScrollReveal>

      <ScrollReveal delay={0.5}>
        <div class="grid grid-cols-2 gap-6 md:grid-cols-4">
          {#each stats as stat (stat.label)}
            <div class="ts-stats-card text-center">
              <div class="text-2xl font-bold ts-text-gradient md:text-3xl">
                <AnimatedCounter value={stat.value} />
              </div>
              <div class="terminal-line mt-2 text-sm text-[#94A3B8]">{stat.label}</div>
            </div>
          {/each}
        </div>
      </ScrollReveal>
    </div>
  </div>

  <div class="animate-bob absolute bottom-8 left-1/2 -translate-x-1/2">
    <div class="flex h-10 w-6 justify-center border-2 border-[#1A1A24] ts-rounded-sm">
      <div class="mt-2 h-3 w-1 animate-pulse ts-rounded-sm bg-[#94A3B8]/50"></div>
    </div>
  </div>
</section>

<section id="features" class="bg-[#0A0A0E] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <ScrollReveal>
      <div class="mb-16 text-center">
        <h2 class="mb-6 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-5xl">
          <span class="terminal-prefix">///</span>
          Why Choose TerraScale
        </h2>
        <p class="mx-auto max-w-3xl text-xl text-[#94A3B8]">
          Built for developers, trusted by enterprises. Experience the next generation of database
          infrastructure.
        </p>
      </div>
    </ScrollReveal>

    <div class="grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-4">
      {#each features as feature, index (feature.title)}
        <ScrollReveal delay={index * 0.08}>
          <div class="hover-lift group h-full p-6 transition-colors duration-200 hover:border-[#2563EB] ts-glass ts-rounded-lg">
            <div class="mb-6 flex h-14 w-14 items-center justify-center border-2 border-[#1A1A24] bg-[#050508] ts-rounded-sm transition-transform duration-200 group-hover:rotate-[2deg] group-hover:scale-110 group-hover:border-[#2563EB]">
              <Icon name={feature.icon} className="h-7 w-7 text-[#2563EB]" />
            </div>
            <h3 class="mb-3 text-xl font-bold text-[#F8FAFC]">{feature.title}</h3>
            <p class="text-sm leading-relaxed text-[#94A3B8]">{feature.description}</p>
          </div>
        </ScrollReveal>
      {/each}
    </div>
  </div>
</section>

<section class="bg-[#050508] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="grid grid-cols-1 items-center gap-16 lg:grid-cols-2">
      <ScrollReveal direction="left">
        <div>
          <h2 class="mb-6 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
            <span class="terminal-prefix">///</span>
            Get started in seconds
          </h2>
          <p class="mb-8 text-xl leading-relaxed text-[#94A3B8]">
            Simple APIs, powerful features. Deploy your database with a single command and start
            building immediately.
          </p>
          <div class="flex items-center space-x-4">
            <RouteLink
              href="/contact"
              class="ts-btn-primary ts-rounded-md inline-flex items-center px-6 py-3 font-bold shadow-offset-blue ts-pressable"
            >
              <span>Start Free Trial</span>
              <Icon name="arrowRight" className="ml-2 h-4 w-4" />
            </RouteLink>

            <RouteLink
              href="/pricing"
              class="inline-flex items-center font-bold text-[#2563EB] transition-colors hover:text-[#60A5FA]"
            >
              <span>View Pricing</span>
              <Icon name="chevronRight" className="ml-1 h-4 w-4" />
            </RouteLink>
          </div>
        </div>
      </ScrollReveal>

      <ScrollReveal direction="right">
        <div class="shadow-offset hover-lift overflow-hidden p-6 ts-glass-frosted ts-rounded-lg">
          <div class="mb-4 flex items-center space-x-2 border-b border-[#1A1A24] pb-4">
            <div class="h-3 w-3 bg-[#F43F5E] ts-rounded-sm"></div>
            <div class="h-3 w-3 bg-[#FBBF24] ts-rounded-sm"></div>
            <div class="h-3 w-3 bg-[#10B981] ts-rounded-sm"></div>
            <div class="flex-1"></div>
            <span class="text-xs text-[#475569]">sdk.ts</span>
            <Icon name="code2" className="ml-2 h-4 w-4 text-[#475569]" />
          </div>
          <div class="ts-rounded-sm bg-[#050508] p-4">
            <pre class="overflow-x-auto text-sm leading-relaxed text-[#F8FAFC]"><code>{codeSnippet}</code></pre>
          </div>
        </div>
      </ScrollReveal>
    </div>
  </div>
</section>

<section class="bg-[#0A0A0E] py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <ScrollReveal>
      <div class="mb-16 text-center">
        <h2 class="mb-4 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
          <span class="terminal-prefix">///</span>
          Trusted by developers worldwide
        </h2>
        <div class="mb-2 flex items-center justify-center space-x-1 text-[#FBBF24]">
          {#each [1, 2, 3, 4, 5] as rating, index (rating)}
            <ScrollReveal direction="none" delay={index * 0.05}>
              <Icon name="star" className="h-5 w-5 fill-current" />
            </ScrollReveal>
          {/each}
        </div>
        <p class="terminal-line text-[#94A3B8]">Loved by 10,000+ developers and growing</p>
      </div>
    </ScrollReveal>

    <div class="grid grid-cols-1 gap-6 md:grid-cols-3">
      {#each testimonials as testimonial, index (testimonial.name)}
        <ScrollReveal delay={index * 0.08}>
          <div class="hover-lift h-full p-6 transition-colors duration-200 hover:border-[#2563EB] ts-glass ts-rounded-lg">
            <div class="mb-6 flex items-center space-x-4">
              <div class="flex h-12 w-12 items-center justify-center bg-[#2563EB] font-bold text-[#050508] ts-rounded-sm">
                {testimonial.avatar}
              </div>
              <div>
                <h4 class="font-bold text-[#F8FAFC]">{testimonial.name}</h4>
                <p class="text-sm text-[#94A3B8]">{testimonial.role}</p>
              </div>
            </div>
            <p class="leading-relaxed text-[#F8FAFC]">"{testimonial.content}"</p>
          </div>
        </ScrollReveal>
      {/each}
    </div>
  </div>
</section>

<section class="relative overflow-hidden bg-[#050508] bg-grid-pattern py-24">
  <ScrollReveal>
    <div class="relative z-10 mx-auto max-w-4xl px-4 text-center sm:px-6 lg:px-8">
      <h2 class="mb-6 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-5xl">
        <span class="terminal-prefix">///</span>
        Ready to build the future?
      </h2>
      <p class="mx-auto mb-10 max-w-2xl text-xl text-[#94A3B8]">
        Join thousands of developers already using TerraScale to build scalable, reliable
        applications.
      </p>
      <div class="flex flex-col items-center justify-center gap-4 sm:flex-row">
        <RouteLink
          href="/contact"
          class="ts-btn-primary ts-rounded-md inline-flex items-center px-8 py-4 font-bold shadow-offset-blue ts-pressable"
        >
          <span>Start Building Today</span>
          <Icon name="arrowRight" className="ml-2 h-5 w-5" />
        </RouteLink>

        <RouteLink
          href="/pricing"
          class="ts-btn-glass ts-rounded-md px-8 py-4 font-bold shadow-offset ts-pressable"
        >
          Explore Pricing
        </RouteLink>
      </div>
    </div>
  </ScrollReveal>
</section>
