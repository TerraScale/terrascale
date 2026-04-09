<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { currentLocation } from '@/lib/router'
  import { headerLinks, navigation } from '@/lib/site'

  let isMenuOpen = false

  $: if ($currentLocation.pathname) {
    isMenuOpen = false
  }

  const navBaseClass =
    'px-3 py-2 ts-rounded-sm text-sm font-medium transition-all duration-200 flex items-center space-x-2 relative'
  const navActiveClass = 'text-[#2563EB] bg-[#2563EB]/10 border border-[#2563EB]/30'
  const navInactiveClass = 'text-[#94A3B8] hover:text-[#F8FAFC] hover:bg-[#0C0C10]'
</script>

<header class="sticky top-0 z-50 border-b border-[#1A1A24] bg-[#050508]/80 bg-grid-pattern backdrop-blur-xl">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="relative flex items-center justify-between py-3">
      <RouteLink href="/" class="flex shrink-0 items-center">
        <img src="/logo-principal.svg" alt="TerraScale" class="h-10 w-auto" />
      </RouteLink>

      <nav class="absolute left-1/2 hidden -translate-x-1/2 items-center space-x-1 md:flex">
        {#each navigation as item (item.href)}
          <RouteLink
            href={item.href}
            match={item.href === '/' ? 'exact' : 'prefix'}
            class={`${navBaseClass} hover-lift`}
            activeClass={navActiveClass}
            inactiveClass={navInactiveClass}
          >
            <Icon name={item.icon} className="h-4 w-4" />
            <span>{item.name}</span>
          </RouteLink>
        {/each}
      </nav>

      <div class="hidden items-center space-x-3 md:flex">
        {#each headerLinks as item (item.name)}
          <a
            href={item.href}
            target="_blank"
            rel="noopener noreferrer"
            class:text-[#94A3B8]={item.name !== 'Dashboard'}
            class:hover:text-[#F8FAFC]={item.name !== 'Dashboard'}
            class="flex items-center space-x-2 px-3 py-2 text-sm font-medium transition-colors duration-200 hover-lift"
            class:ts-btn-primary={item.name === 'Dashboard'}
            class:shadow-offset-blue={item.name === 'Dashboard'}
          >
            <Icon name={item.icon} className="h-4 w-4" />
            <span>{item.name}</span>
          </a>
        {/each}
      </div>

      <button
        type="button"
        class="ts-focus-glow ts-rounded-sm border border-[#1A1A24] p-2 text-[#94A3B8] transition-colors duration-200 hover:bg-[#0C0C10] hover:text-[#F8FAFC] md:hidden"
        aria-expanded={isMenuOpen}
        aria-controls="mobile-navigation"
        aria-label="Toggle navigation menu"
        on:click={() => {
          isMenuOpen = !isMenuOpen
        }}
      >
        {#if isMenuOpen}
          <Icon name="x" className="h-5 w-5" />
        {:else}
          <Icon name="menu" className="h-5 w-5" />
        {/if}
      </button>
    </div>

    {#if isMenuOpen}
      <div id="mobile-navigation" class="border-t border-[#1A1A24] py-4 md:hidden">
        <nav class="flex flex-col space-y-2">
          {#each navigation as item (item.href)}
            <RouteLink
              href={item.href}
              match={item.href === '/' ? 'exact' : 'prefix'}
              class="flex items-center space-x-3 px-3 py-2 ts-rounded-sm text-sm font-medium transition-all duration-200"
              activeClass={navActiveClass}
              inactiveClass={navInactiveClass}
              afterClick={() => {
                isMenuOpen = false
              }}
            >
              <Icon name={item.icon} className="h-4 w-4" />
              <span>{item.name}</span>
            </RouteLink>
          {/each}

          <div class="mt-2 space-y-2 border-t border-[#1A1A24] pt-4">
            {#each headerLinks as item (item.name)}
              <a
                href={item.href}
                target="_blank"
                rel="noopener noreferrer"
                class={`flex items-center ${
                  item.name === 'Dashboard' ? 'justify-center ts-btn-primary shadow-offset-blue' : ''
                } space-x-2 px-3 py-2 ts-rounded-sm text-sm font-medium transition-all duration-200 ${
                  item.name === 'Dashboard'
                    ? ''
                    : 'text-[#94A3B8] hover:bg-[#0C0C10] hover:text-[#F8FAFC]'
                }`}
                on:click={() => {
                  isMenuOpen = false
                }}
              >
                <Icon name={item.icon} className="h-4 w-4" />
                <span>{item.name}</span>
              </a>
            {/each}
          </div>
        </nav>
      </div>
    {/if}
  </div>
</header>
