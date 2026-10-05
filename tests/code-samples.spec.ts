import { expect, test } from 'playwright/test'
import { CODE_SAMPLES, type Lang } from '../src/lib/highlight'

for (const lang of ['csharp', 'typescript'] as Lang[]) {
  test(`${lang} sample renders the current TerraBase document query contract`, async ({ page }) => {
    await page.goto('/')
    await page.getByRole('tab', { name: lang === 'csharp' ? 'C#' : 'TypeScript', exact: true }).click()
    const sample = page.locator('.code__pre code')
    await expect(sample).toHaveText(CODE_SAMPLES[lang])
    const code = await sample.innerText()
    expect(code).toContain('/v1/projects/')
    expect(code).toContain('/databases/')
    expect(code).toContain('/collections/')
    expect(code).toContain('Expected a trusted HTTPS origin')
    for (const name of ['project', 'database', 'collection']) {
      expect(code).toContain(`${lang === 'csharp' ? 'Uri.EscapeDataString' : 'encodeURIComponent'}(${name})`)
    }
    expect(code).toContain('/documents')
    expect(code).toContain('/query')
    expect(code).toContain('team@example.com')
    expect(code).toContain('tb_')
    expect(code).toContain('Bearer')
    expect(code).toContain(lang === 'csharp'
      ? 'staticOrigin ?? await resolveRegion(token).WaitAsync(token)'
      : 'staticOrigin ?? await Promise.race([resolveRegion(signal), cancelled])')
    expect(code).toContain(lang === 'csharp' ? 'deadline.CancelAfter(TimeSpan.FromSeconds(30))' : 'AbortSignal.timeout(30_000)')
    expect(code).toContain(lang === 'csharp' ? 'origin.Scheme != Uri.UriSchemeHttps' : "origin.protocol !== 'https:'")
    expect(code).toContain(lang === 'csharp' ? '!trustedOrigins.Contains(origin)' : '!trustedOrigins.includes(origin.origin)')
    expect(code).toContain('/plan')
    expect(code).toMatch(/limit\s*[:=]\s*25/)
    expect(code).not.toMatch(/tbk1|\/tenants\/|\/shards\/|\/dashboard\/|\/api\/v1\/databases|repository|TerraScaleClient|Tql|gru\.api/i)
  })
}
