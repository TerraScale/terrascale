<script setup>
const year = new Date().getFullYear()

const columns = [
  {
    title: 'Product',
    links: [
      { name: 'Features', href: 'https://terrascale.tech/#features' },
      { name: 'Pricing', href: 'https://terrascale.tech/pricing' },
      { name: 'Changelog', href: '/changelog' },
    ],
  },
  {
    title: 'Company',
    links: [
      { name: 'About', href: 'https://terrascale.tech/about' },
      { name: 'Contact', href: 'https://terrascale.tech/contact' },
      { name: 'Terms', href: '/legal/terms' },
      { name: 'Privacy', href: '/legal/privacy' },
      { name: 'DPA', href: '/legal/dpa' },
    ],
  },
  {
    title: 'Resources',
    links: [
      { name: 'Documentation', href: '/' },
      { name: 'API Reference', href: 'https://api.terrascale.tech/scalar/' },
      { name: 'Status', href: 'https://status.terrascale.tech' },
    ],
  },
  {
    title: 'Support',
    links: [
      { name: 'Support', href: '/legal/support' },
      { name: 'Contact: support@terrascale.tech', href: 'mailto:support@terrascale.tech' },
    ],
  },
]

const socials = [
  { key: 'github', name: 'GitHub', href: 'https://github.com/TerraScale' },
  { key: 'linkedin', name: 'LinkedIn', href: 'https://linkedin.com/company/terrascale' },
  { key: 'email', name: 'Email', href: 'mailto:support@terrascale.tech' },
]

const isExternal = href => href.startsWith('http')
</script>

<template>
  <footer class="docs-footer">
    <div class="docs-footer__container">
      <div class="docs-footer__grid">
        <div class="docs-footer__brand">
          <a class="docs-footer__logo" href="/" aria-label="TerraScale home">
            <img src="/favicon.svg" alt="" width="835" height="942" decoding="async" loading="lazy" />
            <span class="docs-footer__word">TerraScale</span>
          </a>
          <p class="docs-footer__tagline">Multi-region database</p>
          <ul class="docs-footer__socials">
            <li v-for="social in socials" :key="social.key">
              <a
                class="docs-footer__social"
                :href="social.href"
                :target="isExternal(social.href) ? '_blank' : undefined"
                :rel="isExternal(social.href) ? 'external noopener noreferrer' : undefined"
                :aria-label="social.name"
              >
                <svg v-if="social.key === 'github'" class="docs-footer__social-icon" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M12 .297c-6.63 0-12 5.373-12 12c0 5.303 3.438 9.8 8.205 11.385c.6.113.82-.258.82-.577c0-.285-.01-1.04-.015-2.04c-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729c1.205.084 1.838 1.236 1.838 1.236c1.07 1.835 2.809 1.305 3.495.998c.108-.776.417-1.305.76-1.605c-2.665-.3-5.466-1.332-5.466-5.93c0-1.31.465-2.38 1.235-3.22c-.135-.303-.54-1.523.105-3.176c0 0 1.005-.322 3.3 1.23c.96-.267 1.98-.399 3-.405c1.02.006 2.04.138 3 .405c2.28-1.552 3.285-1.23 3.285-1.23c.645 1.653.24 2.873.12 3.176c.765.84 1.23 1.91 1.23 3.22c0 4.61-2.805 5.625-5.475 5.92c.42.36.81 1.096.81 2.22c0 1.606-.015 2.896-.015 3.286c0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"/></svg>
                <svg v-else-if="social.key === 'linkedin'" class="docs-footer__social-icon" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037c-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85c3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.06 2.06 0 0 1-2.063-2.065a2.064 2.064 0 1 1 2.063 2.065m1.782 13.019H3.555V9h3.564zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0z"/></svg>
                <svg v-else class="docs-footer__social-icon" viewBox="0 0 24 24" aria-hidden="true"><g fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5"><path d="m22 7l-8.991 5.727a2 2 0 0 1-2.009 0L2 7"/><rect width="20" height="16" x="2" y="4" rx="2"/></g></svg>
              </a>
            </li>
          </ul>
          <a
            class="docs-footer__cta"
            href="https://dash.terrascale.tech/register"
            target="_blank"
            rel="external noopener noreferrer"
          >Create account</a>
        </div>

        <nav v-for="column in columns" :key="column.title" class="docs-footer__column" :aria-label="column.title">
          <div class="docs-footer__label">{{ column.title }}</div>
          <ul class="docs-footer__list">
            <li v-for="link in column.links" :key="link.href">
              <a
                class="docs-footer__link"
                :href="link.href"
                :target="isExternal(link.href) ? '_blank' : undefined"
                :rel="isExternal(link.href) ? 'external noopener noreferrer' : undefined"
              >{{ link.name }}</a>
            </li>
          </ul>
        </nav>
      </div>
    </div>

    <div class="docs-footer__container">
      <div class="docs-footer__meta">
        <span>© {{ year }} TerraScale. All rights reserved.</span>
        <div class="docs-footer__meta-end">
          <a
            class="docs-footer__status"
            href="https://status.terrascale.tech"
            target="_blank"
            rel="external noopener noreferrer"
          >
            <span class="docs-footer__dot" aria-hidden="true"></span>
            Status
          </a>
        </div>
      </div>
    </div>
  </footer>
</template>
