export default {
  worker: {
    name: 'ts-docs',
    compatibilityDate: '2026-09-11',
    domains: ['docs.terrascale.tech'],
    assets: { notFoundHandling: '404-page' },
    // Cloudflare requires these names to preserve custom-domain hosting and previews.
    workersDev: false,
    previewUrls: true,
  },
  previews: {},
};
