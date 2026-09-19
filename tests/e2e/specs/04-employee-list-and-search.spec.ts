import { test, expect } from '@playwright/test';
import { loginThroughUI, attachApiMonitor } from '../helpers/auth.helper';

test.describe('Employee List & Search/Filter Behavior', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Employee list loads and displays employee table with paginator', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    const employeeApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/employee') && resp.status() === 200
    );

    // Navigate to employee list
    await page.goto('/employee/list');

    // Verify URL
    expect(page.url()).toContain('/employee/list');

    // Verify employee API returned 200 OK
    const apiResponse = await employeeApiPromise;
    expect(apiResponse.status()).toBe(200);

    // Verify table and rows render
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 20000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 20000 });
    const rowCount = await rows.count();
    expect(rowCount).toBeGreaterThan(0);

    // Verify paginator is present and visible
    const paginator = page.locator('mat-paginator');
    await expect(paginator).toBeVisible();

    monitor.assertNoFailures();
  });

  test('Employee search filter updates table results deterministically', async ({ page }) => {
    const monitor = attachApiMonitor(page);

    await page.goto('/employee/list');

    // Wait for initial rows to load
    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 20000 });

    // Mandatory assertion: Name search input must exist and be visible
    const nameInput = page.locator('th[mat-sort-header="name"] input, th.mat-column-name input').first();
    await expect(nameInput).toBeVisible({ timeout: 10000 });

    // Derive deterministic search query from the first row's rendered employee name
    const firstNameCell = rows.first().locator('td.mat-column-name');
    const rawFullName = (await firstNameCell.innerText()).trim();
    expect(rawFullName.length).toBeGreaterThan(0);

    // Extract first term/word from name to filter
    const searchTerm = rawFullName.split(/\s+/)[0];
    expect(searchTerm.length).toBeGreaterThan(0);

    // Prepare response listener for the search query API call
    const searchApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/employee') && resp.status() === 200
    );

    // Focus and fill the search input, dispatching keyup for Angular debounce
    await nameInput.click();
    await nameInput.fill(searchTerm);
    await nameInput.dispatchEvent('keyup');

    // Wait for API to return filtered results
    const searchResponse = await searchApiPromise;
    expect(searchResponse.status()).toBe(200);

    // Verify table results are constrained and non-empty
    await page.waitForTimeout(1000);
    const filteredRows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    const filteredCount = await filteredRows.count();
    expect(filteredCount).toBeGreaterThan(0);

    // Verify every rendered row in the filtered view matches the search term
    for (let i = 0; i < filteredCount; i++) {
      const rowName = (await filteredRows.nth(i).locator('td.mat-column-name').innerText()).trim();
      expect(rowName).toContain(searchTerm);
    }

    monitor.assertNoFailures();
  });
});
