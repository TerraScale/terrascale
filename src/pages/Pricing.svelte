<script lang="ts">
  import PricingSection from '@/components/sections/PricingSection.svelte'
  import PricingFeatureTriad from '@/components/sections/PricingFeatureTriad.svelte'
  import DetailedPricingTable from '@/components/sections/DetailedPricingTable.svelte'
  import PricingFAQ from '@/components/sections/PricingFAQ.svelte'
  import PricingCTA from '@/components/sections/PricingCTA.svelte'
</script>

<PricingSection headingLevel={1} />
<PricingFeatureTriad />
<DetailedPricingTable />
<PricingFAQ />
<PricingCTA />
