import { test, expect } from '@playwright/test';
import { loginThroughUI } from '../helpers/auth.helper';

test.describe('Settings Module Flow & Route Verification', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Settings route (/settings) loads without fatal application crash', async ({ page }) => {
    const pageErrors: Error[] = [];
    page.on('pageerror', err => pageErrors.push(err));

    await page.goto('/settings');
    await page.waitForLoadState('networkidle');

    // Verify URL maintained
    expect(page.url()).toContain('/settings');

    // Verify app root layout is intact (no unhandled JS crash)
    const appRoot = page.locator('app-root');
    await expect(appRoot).toBeVisible();

    // Verify no fatal page-level runtime errors
    expect(pageErrors).toHaveLength(0);
  });
});
