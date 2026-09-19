import { test, expect } from '@playwright/test';
import { loginThroughUI, attachApiMonitor } from '../helpers/auth.helper';

test.describe('Departments Management Flow', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Departments page (/department) loads and renders department table with headcount metrics', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    const departmentApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/department') && resp.status() === 200
    );

    await page.goto('/department');
    await page.waitForLoadState('networkidle');

    expect(page.url()).toContain('/department');

    // Verify department API responded with 200 OK
    const apiResponse = await departmentApiPromise;
    expect(apiResponse.status()).toBe(200);

    // Verify table renders
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 15000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 15000 });
    const count = await rows.count();
    expect(count).toBeGreaterThan(0);

    // Verify headcount column cells (employeesCount) render numerical values
    const countCell = rows.first().locator('td.mat-column-employeesCount');
    await expect(countCell).toBeVisible();
    const countText = (await countCell.innerText()).trim();
    expect(Number.isInteger(parseInt(countText, 10))).toBe(true);

    // Verify paginator is present
    const paginator = page.locator('mat-paginator');
    await expect(paginator).toBeVisible();

    monitor.assertNoFailures();
  });
});
