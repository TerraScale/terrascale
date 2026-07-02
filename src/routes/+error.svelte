<script lang="ts">
  import NotFoundPage from './404/+page.svelte'
</script>

<!-- Re-export the /404 route so unmatched routes and SPA navigation
     errors render the canonical "Not Found" page from a single source. -->
<NotFoundPage />
