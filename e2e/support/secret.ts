import type { Locator } from '@playwright/test';

export async function enterSecret(field: Locator, value: string): Promise<void> {
  await field.evaluate((input: HTMLInputElement, text: string) => {
    input.focus();
    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
}
