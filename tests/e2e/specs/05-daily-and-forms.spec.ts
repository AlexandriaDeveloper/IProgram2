import { test, expect } from '@playwright/test';
import { loginThroughUI, attachApiMonitor } from '../helpers/auth.helper';

test.describe('Daily Batches & Forms Management', () => {

  test.beforeEach(async ({ page }) => {
    await loginThroughUI(page, '2026');
  });

  test('Daily batches page (/daily) loads and renders daily records', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    const dailyApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/daily') && resp.status() === 200
    );

    await page.goto('/daily');
    await page.waitForLoadState('networkidle');

    expect(page.url()).toContain('/daily');

    // Verify daily API responded with 200 OK
    const apiResponse = await dailyApiPromise;
    expect(apiResponse.status()).toBe(200);

    // Verify table renders
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 15000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 15000 });
    const count = await rows.count();
    expect(count).toBeGreaterThan(0);

    monitor.assertNoFailures();
  });

  test('Active Form list navigation from Daily (:id/form) and read-only details flow', async ({ page }) => {
    const monitor = attachApiMonitor(page);

    await page.goto('/daily');
    await page.waitForLoadState('networkidle');

    const dailyRows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(dailyRows.first()).toBeVisible({ timeout: 15000 });

    // Click the info button on the first Daily record to view its forms
    const viewFormsButton = dailyRows.first().locator('button.info-button');
    await expect(viewFormsButton).toBeVisible();

    const formApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/form') && resp.status() === 200
    );

    await viewFormsButton.click();

    // Verify navigated to /daily/:id/form
    await page.waitForURL(/\/daily\/\d+\/form/, { timeout: 15000 });
    expect(page.url()).toMatch(/\/daily\/\d+\/form/);

    // Verify form API responded with 200 OK
    const formApiResponse = await formApiPromise;
    expect(formApiResponse.status()).toBe(200);

    // Verify forms table is rendered
    const formTable = page.locator('table, mat-table');
    await expect(formTable).toBeVisible({ timeout: 10000 });

    // Assert active form rows exist deterministically (verified existing in fixture)
    const formRows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(formRows.first()).toBeVisible({ timeout: 10000 });
    const formCount = await formRows.count();
    expect(formCount).toBeGreaterThan(0);

    // Unconditionally assert read-only navigation to Form Details
    const detailsButton = formRows.first().locator('button.info-button');
    await expect(detailsButton).toBeVisible();
    await detailsButton.click();
    await page.waitForURL(/\/daily\/\d+\/form\/\d+/, { timeout: 15000 });
    expect(page.url()).toMatch(/\/daily\/\d+\/form\/\d+/);

    // Verify form details component loaded read-only without errors
    await page.waitForLoadState('networkidle');
    const detailsContainer = page.locator('app-form-details, table, mat-card, .mat-elevation-z8');
    await expect(detailsContainer.first()).toBeVisible();

    monitor.assertNoFailures();
  });

  test('Archived forms page (/daily/archivedform) loads successfully', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    const archivedApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/form') && resp.status() === 200
    );

    await page.goto('/daily/archivedform');
    await page.waitForLoadState('networkidle');

    expect(page.url()).toContain('/daily/archivedform');

    const apiResponse = await archivedApiPromise;
    expect(apiResponse.status()).toBe(200);

    // Verify table renders
    const table = page.locator('table, mat-table');
    await expect(table).toBeVisible({ timeout: 10000 });

    const rows = page.locator('tr.mat-mdc-row, mat-row, tr[mat-row]');
    await expect(rows.first()).toBeVisible({ timeout: 10000 });

    monitor.assertNoFailures();
  });
});
