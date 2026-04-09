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

  type CodeToken = {
    text: string
    className?: string
  }

  type CodeLine = {
    tokens: CodeToken[]
    highlight?: 'primary' | 'secondary'
  }

  type CodeSnippet = {
    id: 'csharp' | 'go' | 'typescript' | 'rust' | 'python'
    label: string
    fileName: string
    iconPath: string
    buttonClass: string
    fileClass: string
    lines: CodeLine[]
  }

  const escapeHtml = (value: string) =>
    value
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;')

  const renderCodeLine = (tokens: CodeToken[]) =>
    tokens
      .map((token) => {
        const content = escapeHtml(token.text)
        return token.className
          ? `<span class="${token.className}">${content}</span>`
          : `<span>${content}</span>`
      })
      .join('')

  const codeSnippets: CodeSnippet[] = [
    {
      id: 'typescript',
      label: 'TypeScript',
      fileName: 'client.ts',
      iconPath: '/language-icons/typescript.svg',
      buttonClass: 'border-[#3178C6]/30 bg-[#3178C6]/10 text-[#60A5FA] hover:border-[#3178C6]/60 hover:bg-[#3178C6]/15',
      fileClass: 'text-[#60A5FA]',
      lines: [
        {
          tokens: [
            { text: 'import ', className: 'ts-code-token-keyword' },
            { text: '{ ', className: 'ts-code-token-muted' },
            { text: 'TerraScale', className: 'ts-code-token-type' },
            { text: ' } ', className: 'ts-code-token-muted' },
            { text: 'from ', className: 'ts-code-token-keyword' },
            { text: "'@terrascale/sdk'", className: 'ts-code-token-string' },
          ],
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'const ', className: 'ts-code-token-keyword' },
            { text: 'db', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'new ', className: 'ts-code-token-keyword' },
            { text: 'TerraScale', className: 'ts-code-token-type' },
            { text: '({', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '  apiKey', className: 'ts-code-token-property' },
            { text: ': ', className: 'ts-code-token-muted' },
            { text: 'process.env.TERRASCALE_API_KEY', className: 'ts-code-token-accent' },
            { text: ',', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '  region', className: 'ts-code-token-property' },
            { text: ': ', className: 'ts-code-token-muted' },
            { text: "'us-west-2'", className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [{ text: '})', className: 'ts-code-token-muted' }],
          highlight: 'primary',
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [{ text: '// Query active users', className: 'ts-code-token-comment' }],
        },
        {
          tokens: [
            { text: 'const ', className: 'ts-code-token-keyword' },
            { text: 'users', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'await ', className: 'ts-code-token-keyword' },
            { text: 'db', className: 'ts-code-token-variable' },
            { text: '.', className: 'ts-code-token-muted' },
            { text: 'collection', className: 'ts-code-token-function' },
            { text: "('users')", className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '  .', className: 'ts-code-token-muted' },
            { text: 'where', className: 'ts-code-token-function' },
            { text: "('status', '==', 'active')", className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '  .', className: 'ts-code-token-muted' },
            { text: 'limit', className: 'ts-code-token-function' },
            { text: '(10)', className: 'ts-code-token-number' },
          ],
        },
        {
          tokens: [
            { text: '  .', className: 'ts-code-token-muted' },
            { text: 'get', className: 'ts-code-token-function' },
            { text: '()', className: 'ts-code-token-muted' },
          ],
          highlight: 'secondary',
        },
      ],
    },
    {
      id: 'python',
      label: 'Python',
      fileName: 'app.py',
      iconPath: '/language-icons/python.svg',
      buttonClass: 'border-[#3776AB]/30 bg-[#3776AB]/10 text-[#93C5FD] hover:border-[#FFD43B]/50 hover:bg-[#3776AB]/15',
      fileClass: 'text-[#93C5FD]',
      lines: [
        {
          tokens: [
            { text: 'from ', className: 'ts-code-token-keyword' },
            { text: 'terrascale', className: 'ts-code-token-type' },
            { text: ' import ', className: 'ts-code-token-keyword' },
            { text: 'TerraScale', className: 'ts-code-token-type' },
          ],
        },
        { tokens: [{ text: 'import os', className: 'ts-code-token-keyword' }] },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'db', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'TerraScale', className: 'ts-code-token-type' },
            { text: '(', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    api_key', className: 'ts-code-token-property' },
            { text: '=', className: 'ts-code-token-muted' },
            { text: 'os.environ', className: 'ts-code-token-accent' },
            { text: '["TERRASCALE_API_KEY"]', className: 'ts-code-token-string' },
            { text: ',', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    region', className: 'ts-code-token-property' },
            { text: '=', className: 'ts-code-token-muted' },
            { text: '"us-west-2"', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [{ text: ')', className: 'ts-code-token-muted' }],
          highlight: 'primary',
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [{ text: '# Query active users', className: 'ts-code-token-comment' }],
        },
        {
          tokens: [
            { text: 'users', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'db', className: 'ts-code-token-variable' },
            { text: '.collection', className: 'ts-code-token-function' },
            { text: '("users")', className: 'ts-code-token-string' },
            { text: ' \\', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    .where', className: 'ts-code-token-function' },
            { text: '("status", "==", "active")', className: 'ts-code-token-string' },
            { text: ' \\', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    .limit', className: 'ts-code-token-function' },
            { text: '(10)', className: 'ts-code-token-number' },
            { text: ' \\', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    .get', className: 'ts-code-token-function' },
            { text: '()', className: 'ts-code-token-muted' },
          ],
          highlight: 'secondary',
        },
      ],
    },
    {
      id: 'go',
      label: 'Go',
      fileName: 'main.go',
      iconPath: '/language-icons/go.svg',
      buttonClass: 'border-[#00ADD8]/30 bg-[#00ADD8]/10 text-[#67E8F9] hover:border-[#00ADD8]/60 hover:bg-[#00ADD8]/15',
      fileClass: 'text-[#67E8F9]',
      lines: [
        {
          tokens: [
            { text: 'package ', className: 'ts-code-token-keyword' },
            { text: 'main', className: 'ts-code-token-type' },
          ],
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'client', className: 'ts-code-token-variable' },
            { text: ' := ', className: 'ts-code-token-muted' },
            { text: 'terrascale', className: 'ts-code-token-type' },
            { text: '.', className: 'ts-code-token-muted' },
            { text: 'NewClient', className: 'ts-code-token-function' },
            { text: '(terrascale.Config{', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    APIKey', className: 'ts-code-token-property' },
            { text: ': ', className: 'ts-code-token-muted' },
            { text: 'os.Getenv', className: 'ts-code-token-function' },
            { text: '("TERRASCALE_API_KEY")', className: 'ts-code-token-string' },
            { text: ',', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    Region', className: 'ts-code-token-property' },
            { text: ': ', className: 'ts-code-token-muted' },
            { text: '"us-west-2"', className: 'ts-code-token-string' },
            { text: ',', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [{ text: '})', className: 'ts-code-token-muted' }],
          highlight: 'primary',
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'users', className: 'ts-code-token-variable' },
            { text: ', ', className: 'ts-code-token-muted' },
            { text: 'err', className: 'ts-code-token-variable' },
            { text: ' := ', className: 'ts-code-token-muted' },
            { text: 'client', className: 'ts-code-token-variable' },
            { text: '.', className: 'ts-code-token-muted' },
            { text: 'Collection', className: 'ts-code-token-function' },
            { text: '("users").', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '    Where', className: 'ts-code-token-function' },
            { text: '("status", "==", "active").', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '    Limit', className: 'ts-code-token-function' },
            { text: '(10).', className: 'ts-code-token-number' },
          ],
        },
        {
          tokens: [
            { text: '    Get', className: 'ts-code-token-function' },
            { text: '(ctx)', className: 'ts-code-token-muted' },
          ],
          highlight: 'secondary',
        },
      ],
    },
    {
      id: 'rust',
      label: 'Rust',
      fileName: 'lib.rs',
      iconPath: '/language-icons/rust.svg',
      buttonClass: 'border-[#CE422B]/30 bg-[#CE422B]/10 text-[#F97316] hover:border-[#CE422B]/60 hover:bg-[#CE422B]/15',
      fileClass: 'text-[#F97316]',
      lines: [
        {
          tokens: [
            { text: 'use ', className: 'ts-code-token-keyword' },
            { text: 'terrascale', className: 'ts-code-token-type' },
            { text: '::', className: 'ts-code-token-muted' },
            { text: 'TerraScale', className: 'ts-code-token-type' },
            { text: ';', className: 'ts-code-token-muted' },
          ],
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'let ', className: 'ts-code-token-keyword' },
            { text: 'client', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'TerraScale', className: 'ts-code-token-type' },
            { text: '::', className: 'ts-code-token-muted' },
            { text: 'builder', className: 'ts-code-token-function' },
            { text: '()', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'api_key', className: 'ts-code-token-function' },
            { text: '(env::var("TERRASCALE_API_KEY")?)', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'region', className: 'ts-code-token-function' },
            { text: '("us-west-2")', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'build', className: 'ts-code-token-function' },
            { text: '()?', className: 'ts-code-token-muted' },
          ],
          highlight: 'primary',
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'let ', className: 'ts-code-token-keyword' },
            { text: 'users', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'client', className: 'ts-code-token-variable' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'collection', className: 'ts-code-token-function' },
            { text: '("users")', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'where_eq', className: 'ts-code-token-function' },
            { text: '("status", "active")', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'limit', className: 'ts-code-token-function' },
            { text: '(10)', className: 'ts-code-token-number' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'get', className: 'ts-code-token-function' },
            { text: '().await?;', className: 'ts-code-token-muted' },
          ],
          highlight: 'secondary',
        },
      ],
    },
    {
      id: 'csharp',
      label: 'C#',
      fileName: 'Program.cs',
      iconPath: '/language-icons/csharp.svg',
      buttonClass: 'border-[#9B4F96]/30 bg-[#9B4F96]/10 text-[#D8B4FE] hover:border-[#9B4F96]/60 hover:bg-[#9B4F96]/15',
      fileClass: 'text-[#D8B4FE]',
      lines: [
        {
          tokens: [
            { text: 'using ', className: 'ts-code-token-keyword' },
            { text: 'TerraScale', className: 'ts-code-token-type' },
            { text: ';', className: 'ts-code-token-muted' },
          ],
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'var ', className: 'ts-code-token-keyword' },
            { text: 'client', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'new ', className: 'ts-code-token-keyword' },
            { text: 'TerraScaleClient', className: 'ts-code-token-type' },
            { text: '(new ', className: 'ts-code-token-muted' },
            { text: 'TerraScaleOptions', className: 'ts-code-token-type' },
          ],
        },
        {
          tokens: [{ text: '{', className: 'ts-code-token-muted' }],
        },
        {
          tokens: [
            { text: '    ApiKey', className: 'ts-code-token-property' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'Environment.GetEnvironmentVariable', className: 'ts-code-token-function' },
            { text: '("TERRASCALE_API_KEY")!', className: 'ts-code-token-string' },
            { text: ',', className: 'ts-code-token-muted' },
          ],
        },
        {
          tokens: [
            { text: '    Region', className: 'ts-code-token-property' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: '"us-west-2"', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [{ text: '});', className: 'ts-code-token-muted' }],
          highlight: 'primary',
        },
        { tokens: [{ text: '' }] },
        {
          tokens: [
            { text: 'var ', className: 'ts-code-token-keyword' },
            { text: 'users', className: 'ts-code-token-variable' },
            { text: ' = ', className: 'ts-code-token-muted' },
            { text: 'await ', className: 'ts-code-token-keyword' },
            { text: 'client', className: 'ts-code-token-variable' },
            { text: '.Collections', className: 'ts-code-token-function' },
            { text: '["users"]', className: 'ts-code-token-string' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'Where', className: 'ts-code-token-function' },
            {
              text: '("status", FilterOperator.Equals, "active")',
              className: 'ts-code-token-string',
            },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'Limit', className: 'ts-code-token-function' },
            { text: '(10)', className: 'ts-code-token-number' },
          ],
        },
        {
          tokens: [
            { text: '    .', className: 'ts-code-token-muted' },
            { text: 'GetAsync', className: 'ts-code-token-function' },
            { text: '();', className: 'ts-code-token-muted' },
          ],
          highlight: 'secondary',
        },
      ],
    },
  ]

  let activeCodeSnippetId: CodeSnippet['id'] = 'typescript'

  $: activeCodeSnippet =
    codeSnippets.find((snippet) => snippet.id === activeCodeSnippetId) ?? codeSnippets[0]
</script>

<div class="relative overflow-hidden bg-[#050508]">
  <div
    class="pointer-events-none absolute inset-0 opacity-80"
    style="background:
      radial-gradient(ellipse at top, rgba(59, 130, 246, 0.18) 0%, rgba(5, 5, 8, 0) 52%),
      radial-gradient(circle at 20% 20%, rgba(37, 99, 235, 0.14) 0%, rgba(5, 5, 8, 0) 34%),
      radial-gradient(circle at 80% 18%, rgba(16, 185, 129, 0.1) 0%, rgba(5, 5, 8, 0) 28%),
      radial-gradient(circle at 50% 65%, rgba(8, 145, 178, 0.08) 0%, rgba(5, 5, 8, 0) 34%);"
  ></div>
  <div class="pointer-events-none absolute inset-0 bg-grid-pattern opacity-55"></div>
  <div class="pointer-events-none scanlines absolute inset-0 opacity-20"></div>

  <div class="pointer-events-none animate-float-slow absolute left-[15%] top-[12%] h-96 w-96 border border-[#2563EB]/20 ts-rounded-md opacity-30">
    <div class="absolute inset-0 bg-gradient-to-br from-[#60A5FA]/10 to-transparent"></div>
  </div>
  <div class="pointer-events-none animate-float-reverse absolute right-[15%] top-[32%] h-80 w-80 border border-[#10B981]/20 ts-rounded-md opacity-30">
    <div class="absolute inset-0 bg-gradient-to-br from-[#34D399]/8 to-transparent"></div>
  </div>
  <div class="pointer-events-none animate-float-slow absolute bottom-[18%] left-[8%] h-72 w-72 rounded-full border border-[#2563EB]/12 bg-[#2563EB]/6 blur-2xl"></div>
  <div class="pointer-events-none animate-float-reverse absolute bottom-[8%] right-[10%] h-64 w-64 rounded-full border border-[#0891B2]/12 bg-[#0891B2]/6 blur-2xl"></div>

  <div class="relative z-10">
    <section class="relative flex min-h-[calc(100svh-var(--ts-header-height))] items-center justify-center overflow-hidden">
      <div class="mx-auto max-w-[1440px] px-4 py-16 sm:px-6 md:py-[4.5rem] lg:px-8 lg:py-20">
        <div class="mx-auto max-w-4xl text-center">
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
                class="ts-btn-primary ts-rounded-md inline-flex items-center px-8 py-4 text-[0.8125rem] font-bold shadow-offset-blue ts-pressable"
              >
                <span>Start Building Free</span>
                <Icon name="arrowRight" className="ml-2 h-5 w-5" />
              </RouteLink>

              <a
                href="https://docs.terrascale.tech"
                target="_blank"
                rel="noopener noreferrer"
                class="ts-btn-glass ts-rounded-md inline-flex items-center px-8 py-4 text-[0.8125rem] font-bold shadow-offset ts-pressable"
              >
                <Icon name="bookOpen" className="mr-2 h-5 w-5" />
                <span>View Documentation</span>
              </a>
            </div>
          </ScrollReveal>

          <ScrollReveal delay={0.5}>
            <div class="mb-8 inline-flex items-center px-4 py-2 text-sm font-medium text-[#F8FAFC] hover-lift ts-glass ts-rounded-sm">
              <span class="mr-3 h-2 w-2 animate-pulse ts-glow-secondary ts-rounded-sm bg-[#10B981]"></span>
              <Icon name="database" className="mr-2 h-4 w-4 text-[#2563EB]" />
              <span>Database as a Service Platform</span>
            </div>
          </ScrollReveal>

          <ScrollReveal delay={0.6}>
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

<section id="features" class="py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <ScrollReveal>
      <div class="mb-16 text-center">
        <h2 class="mb-6 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-5xl">
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

<section class="py-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="grid grid-cols-1 items-center gap-16 lg:grid-cols-2">
      <ScrollReveal direction="left">
        <div>
          <h2 class="mb-6 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
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
          <div class="mb-4 flex flex-wrap gap-2">
            {#each codeSnippets as snippet (snippet.id)}
              <button
                type="button"
                class={`inline-flex items-center gap-1.5 border px-2 py-1.5 text-[0.65rem] font-bold uppercase tracking-[0.14em] transition-all duration-200 ts-rounded-sm ${
                  activeCodeSnippet.id === snippet.id
                    ? `${snippet.buttonClass} shadow-offset`
                    : 'border-[#1A1A24] bg-[#050508]/80 text-[#94A3B8] hover:border-[#334155] hover:text-[#F8FAFC]'
                }`}
                on:click={() => {
                  activeCodeSnippetId = snippet.id
                }}
              >
                <img
                  src={snippet.iconPath}
                  alt=""
                  class={`object-contain ${snippet.id === 'go' ? 'h-3 w-4' : 'h-3.5 w-3.5'}`}
                  aria-hidden="true"
                />
                <span>{snippet.label}</span>
              </button>
            {/each}
          </div>

          <div class="mb-4 flex items-center space-x-2 border-b border-[#1A1A24] pb-4">
            <div class="h-3 w-3 bg-[#F43F5E] ts-rounded-sm"></div>
            <div class="h-3 w-3 bg-[#FBBF24] ts-rounded-sm"></div>
            <div class="h-3 w-3 bg-[#10B981] ts-rounded-sm"></div>
            <div class="flex-1"></div>
            <span class={`text-xs font-bold uppercase tracking-[0.14em] ${activeCodeSnippet.fileClass}`}>
              {activeCodeSnippet.fileName}
            </span>
            <Icon name="code2" className="ml-2 h-4 w-4 text-[#475569]" />
          </div>
          <div class="ts-rounded-sm bg-[#050508] p-4">
            <div class="overflow-x-auto text-sm leading-relaxed">
              <code class="block min-w-max">
                {#each activeCodeSnippet.lines as line, index (`${activeCodeSnippet.id}-${index}`)}
                  <div
                    class={`ts-code-line ${
                      line.highlight === 'primary'
                        ? 'ts-code-line-highlight-primary'
                        : line.highlight === 'secondary'
                          ? 'ts-code-line-highlight-secondary'
                          : ''
                    }`}
                  >
                    <span class="ts-code-line-number">{index + 1}</span>
                    <span class="ts-code-line-content">{@html renderCodeLine(line.tokens)}</span>
                  </div>
                {/each}
              </code>
            </div>
          </div>
        </div>
      </ScrollReveal>
    </div>
  </div>
</section>

{#if false}
  <section class="py-24">
    <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
      <ScrollReveal>
        <div class="mb-16 text-center">
          <h2 class="mb-4 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
            Trusted by developers worldwide
          </h2>
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
{/if}

<section class="relative overflow-hidden py-24">
  <ScrollReveal>
    <div class="relative z-10 mx-auto max-w-4xl px-4 text-center sm:px-6 lg:px-8">
      <h2 class="mb-6 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-5xl">
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
  </div>
</div>
