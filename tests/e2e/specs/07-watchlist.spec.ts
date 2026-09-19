import { test, expect } from '@playwright/test';
import { loginThroughUI, attachApiMonitor } from '../helpers/auth.helper';

test.describe('Watchlist Management Flow', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Watchlist page (/watchlist) loads and renders watchlist entries with paginator verification', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    const watchlistApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/watchlist') && resp.status() === 200
    );

    await page.goto('/watchlist');
    await page.waitForLoadState('networkidle');

    expect(page.url()).toContain('/watchlist');

    // Verify watchlist API responded with 200 OK
    const apiResponse = await watchlistApiPromise;
    expect(apiResponse.status()).toBe(200);

    // Verify table renders
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 10000 });

    // Verify paginator element is rendered and visible
    const paginator = page.locator('mat-paginator');
    await expect(paginator).toBeVisible();

    // Verify paginator page-size or range label is displayed
    const rangeLabel = page.locator('.mat-mdc-paginator-range-label');
    await expect(rangeLabel).toBeVisible();
    const rangeText = await rangeLabel.innerText();
    expect(rangeText.length).toBeGreaterThan(0);

    // If next page button is enabled, verify paginator interaction triggers non-mutating page navigation
    const nextButton = page.locator('button.mat-mdc-paginator-navigation-next:not([disabled])');
    if (await nextButton.isVisible()) {
      const nextPageApiPromise = page.waitForResponse(
        resp => resp.url().includes('/api/watchlist') && resp.status() === 200
      );
      await nextButton.click();
      const nextPageResponse = await nextPageApiPromise;
      expect(nextPageResponse.status()).toBe(200);
    }

    monitor.assertNoFailures();
  });
});
