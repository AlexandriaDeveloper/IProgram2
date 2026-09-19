import { Page, expect } from '@playwright/test';

export const TEST_CONFIG = {
  username: process.env.E2E_USERNAME || 'bob',
  password: process.env.E2E_PASSWORD || 'Pass123$',
  defaultYear: '2026',
};

/**
 * Performs browser-level login through the Angular UI
 */
export async function loginThroughUI(page: Page, year: string = '2026') {
  await page.goto('/account/login');
  await page.waitForLoadState('networkidle');

  // Fill credentials
  const usernameInput = page.locator('input[formControlName="username"]');
  const passwordInput = page.locator('input[formControlName="password"]');

  await usernameInput.fill(TEST_CONFIG.username);
  await passwordInput.fill(TEST_CONFIG.password);

  // Select database year if needed
  const dbSelect = page.locator('mat-select[formControlName="database"]');
  await expect(dbSelect).toBeVisible({ timeout: 10000 });
  
  const currentText = await dbSelect.innerText();
  if (!currentText.includes(year)) {
    await dbSelect.click();
    const option = page.locator(`mat-option:has-text("${year}")`);
    await option.waitFor({ state: 'visible', timeout: 5000 });
    await option.click();
    // Wait for overlay to close
    await page.locator('.cdk-overlay-backdrop').waitFor({ state: 'hidden', timeout: 5000 }).catch(() => {});
  }

  // Click login submit button
  const submitButton = page.locator('button[type="submit"]');
  await submitButton.click();

  // Wait for redirect away from login or to dashboard
  await page.waitForURL((url) => !url.pathname.includes('/account/login'), { timeout: 20000 });
  await page.waitForLoadState('networkidle');
}
