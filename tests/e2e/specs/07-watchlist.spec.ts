import { test, expect } from '@playwright/test';
import { loginThroughUI } from '../helpers/auth.helper';

test.describe('Watchlist Management Flow', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Watchlist page (/watchlist) loads and renders watchlist entries', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('console', msg => {
      const text = msg.text();
      const isKnownSignalR = text.includes('migrationHub') || text.includes('negotiation') || text.includes('405');
      if (msg.type() === 'error' && !isKnownSignalR) {
        consoleErrors.push(text);
      }
    });

    await page.goto('/watchlist');
    await page.waitForLoadState('networkidle');

    expect(page.url()).toContain('/watchlist');

    // Verify table renders
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 10000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 10000 });
    const count = await rows.count();
    expect(count).toBeGreaterThan(0);

    expect(consoleErrors).toHaveLength(0);
  });
});
