import { test, expect } from '@playwright/test';
import { loginThroughUI } from '../helpers/auth.helper';

test.describe('Daily Batches & Forms Management', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Daily batches page (/daily) loads and renders daily records', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('console', msg => {
      const text = msg.text();
      const isKnownSignalR = text.includes('migrationHub') || text.includes('negotiation') || text.includes('405');
      if (msg.type() === 'error' && !isKnownSignalR) {
        consoleErrors.push(text);
      }
    });

    await page.goto('/daily');
    await page.waitForLoadState('networkidle');

    expect(page.url()).toContain('/daily');

    // Verify table renders
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 15000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 15000 });
    const count = await rows.count();
    expect(count).toBeGreaterThan(0);

    expect(consoleErrors).toHaveLength(0);
  });

  test('Archived forms page (/daily/archivedform) loads successfully', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('console', msg => {
      const text = msg.text();
      const isKnownSignalR = text.includes('migrationHub') || text.includes('negotiation') || text.includes('405');
      if (msg.type() === 'error' && !isKnownSignalR) {
        consoleErrors.push(text);
      }
    });

    await page.goto('/daily/archivedform');
    await page.waitForLoadState('networkidle');

    expect(page.url()).toContain('/daily/archivedform');

    // Verify table renders
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 10000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 10000 });

    expect(consoleErrors).toHaveLength(0);
  });
});
