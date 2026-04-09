<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { footerHighlights, footerLinks, socialLinks, supportChannels } from '@/lib/site'
</script>

<footer class="border-t border-[#1A1A24] bg-[#050508] bg-grid-pattern">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="py-8">
      <div class="grid grid-cols-1 gap-8 md:grid-cols-2 lg:grid-cols-5">
        <div class="lg:col-span-2">
          <RouteLink href="/" class="mb-4 flex items-center">
            <img src="/logo-white.svg" alt="TerraScale" class="h-10 w-auto" />
          </RouteLink>

          <p class="mb-4 max-w-sm text-sm text-[#94A3B8]">
            Empowering businesses with secure, scalable, and innovative cloud solutions. Scale your
            tech, effortlessly.
          </p>

          <div class="mb-4 space-y-2 text-sm text-[#94A3B8]">
            {#each supportChannels as channel (channel.label)}
              <div class="flex items-center space-x-2">
                <Icon name={channel.icon} className="h-3 w-3 text-[#F8FAFC]" />
                <a
                  href={channel.href}
                  target={channel.href.startsWith('http') ? '_blank' : undefined}
                  rel={channel.href.startsWith('http') ? 'noopener noreferrer' : undefined}
                  class="inline-flex items-center gap-2 transition-colors duration-200 hover:text-[#F8FAFC]"
                >
                  <Icon name="chevronRight" className="h-3 w-3 text-[#F8FAFC]" />
                  {channel.label}
                </a>
              </div>
            {/each}
          </div>

          <div class="flex space-x-3">
            {#each socialLinks as social (social.name)}
              <a
                href={social.href}
                target="_blank"
                rel="noopener noreferrer"
                aria-label={social.name}
                class="hover-lift hover-lift-active ts-rounded-sm border border-[#1A1A24] bg-[#080810] p-2 transition-all duration-200 hover:border-[#F8FAFC]"
              >
                <Icon name={social.icon} className="h-4 w-4 text-[#F8FAFC] transition-colors hover:text-[#CBD5E1]" />
              </a>
            {/each}
          </div>
        </div>

        {#each Object.entries(footerLinks) as [category, links] (category)}
          <div>
            <h3 class="mb-3 text-sm font-semibold text-[#F8FAFC]">
              {category}
            </h3>
            <ul class="space-y-2">
              {#each links as link (link.name)}
                <li>
                  {#if link.href.startsWith('http')}
                    <a
                      href={link.href}
                      target="_blank"
                      rel="noopener noreferrer"
                      class="inline-flex items-center gap-2 text-sm text-[#94A3B8] transition-colors duration-200 hover:text-[#F8FAFC]"
                    >
                      <Icon name="chevronRight" className="h-3 w-3 text-[#F8FAFC]" />
                      {link.name}
                    </a>
                  {:else}
                    <RouteLink
                      href={link.href}
                      class="inline-flex items-center gap-2 text-sm text-[#94A3B8] transition-colors duration-200 hover:text-[#F8FAFC]"
                    >
                      <Icon name="chevronRight" className="h-3 w-3 text-[#F8FAFC]" />
                      {link.name}
                    </RouteLink>
                  {/if}
                </li>
              {/each}
            </ul>
          </div>
        {/each}
      </div>
    </div>

    <div class="border-t border-[#1A1A24] py-4">
      <div class="flex flex-col items-center justify-between space-y-2 md:flex-row md:space-y-0">
        <p class="text-sm text-[#475569]">© {new Date().getFullYear()} TerraScale. All rights reserved.</p>

        <div class="flex flex-wrap items-center justify-center gap-4 text-sm text-[#475569]">
          {#each footerHighlights as highlight (highlight.label)}
            <div class={highlight.className}>
              {#if highlight.label === '99.99% Uptime'}
                <div class="h-2 w-2 animate-pulse ts-rounded-sm bg-[#10B981]"></div>
              {/if}
              <Icon name={highlight.icon} className="h-3 w-3" />
              <span>{highlight.label}</span>
            </div>
          {/each}
        </div>
      </div>
    </div>
  </div>
</footer>
