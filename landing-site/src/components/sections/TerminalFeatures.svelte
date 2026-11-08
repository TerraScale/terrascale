<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const protocols = ['psql', 'HTTP', 'WebSocket', 'C# SDK', 'TypeScript SDK', 'CLI', 'MCP']
  const mcpTools = ['list_databases', 'execute_sql', 'get_usage', 'list_backups']
</script>

<section class="tf ts-section" id="features" use:reveal>
  <div class="ts-container">
    <div class="tf__head">
      <h2 class="ts-section-title">
        {$dict.features.titleLead}<br />
        <span class="tf__accent">{$dict.features.titleAccent}</span>
      </h2>
    </div>

    <div class="tf__rows">
      <article class="tf__row">
        <div class="tf__copy">
          <h3 class="tf__title">{$dict.terminalFeatures.rows[0].title}</h3>
          <p class="tf__text">{$dict.terminalFeatures.rows[0].text}</p>
        </div>
        <div class="tf__visual">
          <div class="tf__map">
            <span class="tf__hub"><RegionFlag code="gru" />gru · home</span>
            <span class="tf__link" aria-hidden="true"></span>
            <div class="tf__replicas">
              <span class="tf__replica"><RegionFlag code="sjc" />sjc</span>
              <span class="tf__replica"><RegionFlag code="fra" />fra</span>
            </div>
          </div>
        </div>
      </article>

      <article class="tf__row tf__row--flip">
        <div class="tf__copy">
          <h3 class="tf__title">{$dict.terminalFeatures.rows[1].title}</h3>
          <p class="tf__text">{$dict.terminalFeatures.rows[1].text}</p>
        </div>
        <div class="tf__visual">
          <ul class="tf__chips">
            {#each protocols as protocol (protocol)}
              <li class="tf__chip">{protocol}</li>
            {/each}
          </ul>
        </div>
      </article>

      <article class="tf__row">
        <div class="tf__copy">
          <h3 class="tf__title">{$dict.terminalFeatures.rows[2].title}</h3>
          <p class="tf__text">{$dict.terminalFeatures.rows[2].text}</p>
        </div>
        <div class="tf__visual">
          <div class="tf__agent">
            <p class="tf__prompt">{$dict.code.agent.prompts[1]}</p>
            <ul class="tf__tools">
              {#each mcpTools as tool (tool)}
                <li class="tf__tool">{tool}</li>
              {/each}
            </ul>
          </div>
        </div>
      </article>
    </div>
  </div>
</section>

<style>
  .tf {
    border-top: 1px solid var(--ts-divider);
  }

  .tf__head {
    max-width: 680px;
    margin: 0 auto var(--ts-section-gap);
    text-align: center;
  }

  .tf__accent {
    color: var(--ts-primary-light);
  }

  .tf__rows {
    display: grid;
    gap: var(--ts-section-gap);
    max-width: 1040px;
    margin: 0 auto;
  }

  .tf__row {
    display: grid;
    grid-template-columns: minmax(0, 6fr) minmax(0, 5fr);
    gap: clamp(24px, 4vw, 56px);
    align-items: center;
  }

  .tf__row--flip .tf__copy {
    order: 2;
  }

  .tf__title {
    margin: 0 0 12px;
    font-size: 24px;
    line-height: 32px;
    font-weight: 600;
    letter-spacing: -0.02em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .tf__text {
    margin: 0;
    max-width: 52ch;
    font-size: 15px;
    line-height: 1.7;
    color: var(--ts-text-muted);
    text-wrap: pretty;
  }

  .tf__visual {
    min-width: 0;
  }

  .tf__map {
    display: grid;
    justify-items: center;
    gap: 12px;
    padding: 28px 20px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
  }

  .tf__hub,
  .tf__replica {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    height: 30px;
    padding: 0 12px;
    border: 1px solid rgba(var(--ts-primary-rgb), 0.35);
    border-radius: var(--ts-radius-md);
    background: rgba(var(--ts-primary-rgb), 0.08);
    color: var(--ts-text);
    font: 500 13px / 1 var(--ts-font-mono);
  }

  .tf__link {
    width: 1px;
    height: 26px;
    background: repeating-linear-gradient(
      to bottom,
      var(--ts-text-muted) 0 4px,
      transparent 4px 8px
    );
    opacity: 0.5;
  }

  .tf__replicas {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 8px;
  }

  .tf__replica {
    border-color: var(--ts-divider);
    background: transparent;
    color: var(--ts-text-muted);
  }

  .tf__chips {
    list-style: none;
    margin: 0;
    padding: 24px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
  }

  .tf__chip {
    display: inline-flex;
    align-items: center;
    height: 30px;
    padding: 0 12px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-md);
    color: var(--ts-text);
    font: 500 13px / 1 var(--ts-font-mono);
  }

  .tf__agent {
    display: grid;
    gap: 14px;
    padding: 24px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
  }

  .tf__prompt {
    margin: 0;
    padding: 12px 14px;
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    border-radius: 10px 10px 10px 2px;
    background: rgba(var(--ts-primary-rgb), 0.07);
    color: var(--ts-text);
    font-size: 14px;
    line-height: 1.6;
    text-align: left;
  }

  .tf__tools {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .tf__tool {
    display: inline-flex;
    align-items: center;
    height: 26px;
    padding: 0 8px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-sm);
    color: var(--ts-text-muted);
    font: 400 12px / 1 var(--ts-font-mono);
  }

  @media (max-width: 900px) {
    .tf__row {
      grid-template-columns: minmax(0, 1fr);
      gap: 24px;
    }

    .tf__row--flip .tf__copy {
      order: 0;
    }
  }
</style>
