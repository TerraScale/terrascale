<script lang="ts">
  import EditorialHeader from '@/components/sections/EditorialHeader.svelte'
  import Icon from '@/components/Icon.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const mcpTools = ['list_databases', 'execute_sql', 'get_usage', 'list_backups']

  $: bullets = $dict.code.bullets
  $: agent = $dict.code.agent
</script>

<section class="ed-code ts-section" use:reveal>
  <div class="ts-container">
    <EditorialHeader index="03" lead={$dict.code.headlineLead} accent={$dict.code.headlineAccent} />

    <div class="ed-code__grid">
      <ul class="ed-code__bullets">
        {#each bullets as bullet, i (bullet)}
          <li class="ed-code__bullet">
            <span class="ed-code__bullet-num">{String(i + 1).padStart(2, '0')}</span>
            <span class="ed-code__bullet-text">{bullet}</span>
            <Icon name="check" className="ed-code__bullet-icon" />
          </li>
        {/each}
      </ul>

      <div class="ed-code__agent">
        <h3 class="ed-code__agent-title">
          <Icon name="messageSquare" className="ed-code__agent-icon" />
          {agent.title}
        </h3>
        <p class="ed-code__agent-text">{agent.text}</p>
        <ul class="ed-code__prompts">
          {#each agent.prompts as prompt (prompt)}
            <li class="ed-code__prompt">{prompt}</li>
          {/each}
        </ul>
        <ul class="ed-code__tools">
          {#each mcpTools as tool (tool)}
            <li><code class="ed-code__tool">{tool}</code></li>
          {/each}
        </ul>
      </div>
    </div>
  </div>
</section>

<style>
  .ed-code__grid {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
    gap: clamp(40px, 6vw, 96px);
    align-items: start;
  }

  .ed-code__bullets {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .ed-code__bullet {
    display: grid;
    grid-template-columns: 40px minmax(0, 1fr) 20px;
    gap: 12px;
    align-items: center;
    padding-block: 20px;
    border-top: 1px solid var(--ts-divider);
    color: var(--ts-text);
    font-size: 16px;
    line-height: 1.5;
  }

  .ed-code__bullet-num {
    font-family: var(--ts-font-mono);
    font-size: 13px;
    color: var(--ts-text-muted);
  }

  :global(.ed-code__bullet-icon) {
    width: 16px;
    height: 16px;
    color: var(--ts-primary-light);
  }

  .ed-code__agent {
    display: grid;
    gap: 16px;
    padding: clamp(24px, 3vw, 40px);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
  }

  .ed-code__agent-title {
    display: flex;
    align-items: flex-start;
    gap: 12px;
    margin: 0;
    font-size: 20px;
    font-weight: 600;
    color: var(--ts-text);
    letter-spacing: -0.015em;
    line-height: 1.35;
    text-wrap: balance;
  }

  :global(.ed-code__agent-icon) {
    flex-shrink: 0;
    width: 18px;
    height: 18px;
    margin-top: 4px;
    color: var(--ts-primary-light);
  }

  .ed-code__agent-text {
    margin: 0;
    font-size: 14px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    max-width: 64ch;
    text-wrap: pretty;
  }

  .ed-code__prompts {
    list-style: none;
    margin: 8px 0 0;
    padding: 0;
    display: grid;
    gap: 8px;
  }

  .ed-code__prompt {
    padding: 12px 16px;
    border: 1px solid var(--ts-divider);
    border-left: 2px solid var(--ts-primary);
    border-radius: 0 var(--ts-radius-md) var(--ts-radius-md) var(--ts-radius-md);
    background: var(--ts-bg-base);
    color: var(--ts-text);
    font-family: var(--ts-font-mono);
    font-size: 13px;
    line-height: 1.6;
  }

  .ed-code__tools {
    list-style: none;
    margin: 8px 0 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .ed-code__tool {
    display: inline-flex;
    align-items: center;
    height: 26px;
    padding: 0 8px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-sm);
    color: var(--ts-text-muted);
    font-family: var(--ts-font-mono);
    font-size: 12px;
    line-height: 1;
  }

  @media (max-width: 1023px) {
    .ed-code__grid {
      grid-template-columns: minmax(0, 1fr);
      gap: 40px;
    }
  }

  @media (max-width: 640px) {
    .ed-code__bullet {
      grid-template-columns: 32px minmax(0, 1fr) 20px;
      font-size: 15px;
    }
  }
</style>
