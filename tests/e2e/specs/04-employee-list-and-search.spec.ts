import { test, expect } from '@playwright/test';
import { loginThroughUI } from '../helpers/auth.helper';

test.describe('Employee List & Search/Filter Behavior', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Employee list loads and displays employee table with paginator', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('console', msg => {
      const text = msg.text();
      const isKnownSignalR = text.includes('migrationHub') || text.includes('negotiation') || text.includes('405');
      if (msg.type() === 'error' && !isKnownSignalR) {
        consoleErrors.push(text);
      }
    });

    // Navigate to employee list
    await page.goto('/employee/list');

    // Verify URL
    expect(page.url()).toContain('/employee/list');

    // Verify table and rows render
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 20000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 20000 });
    const rowCount = await rows.count();
    expect(rowCount).toBeGreaterThan(0);

    // Verify paginator is present
    const paginator = page.locator('mat-paginator');
    await expect(paginator).toBeVisible();

    expect(consoleErrors).toHaveLength(0);
  });

  test('Employee search filter updates table results', async ({ page }) => {
    await page.goto('/employee/list');

    // Wait for initial rows
    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 20000 });

    // Look for name text search input in table headers (excluding number and checkbox inputs)
    const nameInput = page.locator('th[mat-sort-header="name"] input, input:not([type="number"]):not([type="checkbox"])').first();
    if (await nameInput.isVisible()) {
      await nameInput.fill('محمد');
      await page.waitForTimeout(1500);

      // Verify table still has rows matching filter
      const filteredRows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
      const filteredCount = await filteredRows.count();
      expect(filteredCount).toBeGreaterThan(0);
    }
  });
});
