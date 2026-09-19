import { test, expect } from '@playwright/test';
import { loginThroughUI, attachApiMonitor } from '../helpers/auth.helper';

test.describe('Dashboard Load & Data Rendering', () => {

  test('Dashboard loads successfully with statistical cards and charts', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    const dashboardApiPromise = page.waitForResponse(
      resp => resp.url().includes('/api/dashboard') && resp.status() === 200
    );

    // Login through UI
    await loginThroughUI(page, '2026');

    // Verify current route is dashboard
    await expect(page).toHaveURL(/localhost:5000\/?$/);

    // Verify dashboard API response was 200 OK and contains data
    const dashboardResponse = await dashboardApiPromise;
    expect(dashboardResponse.status()).toBe(200);
    const data = await dashboardResponse.json();
    expect(data).toHaveProperty('totalEmployees');

    // Verify dashboard statistics elements exist (.kpi-card)
    const cards = page.locator('.kpi-card');
    await expect(cards.first()).toBeVisible({ timeout: 15000 });
    const cardCount = await cards.count();
    expect(cardCount).toBeGreaterThanOrEqual(2);

    // Verify live employee count is displayed
    const employeeCard = page.locator('.kpi-card:has-text("الموظفين")');
    await expect(employeeCard).toBeVisible();

    monitor.assertNoFailures();
  });

  test('Dashboard date filter controls are present and interactive (Non-mutating verification)', async ({ page }) => {
    const monitor = attachApiMonitor(page);

    await loginThroughUI(page, '2026');

    // Mandatory assertion: mat-date-range-input must exist and be visible
    const dateRangeInput = page.locator('mat-date-range-input');
    await expect(dateRangeInput).toBeVisible({ timeout: 10000 });

    const startInput = page.locator('mat-date-range-input input[matStartDate], mat-date-range-input input[formControlName="start"]');
    const endInput = page.locator('mat-date-range-input input[matEndDate], mat-date-range-input input[formControlName="end"]');
    await expect(startInput).toBeVisible();
    await expect(endInput).toBeVisible();

    // Verify datepicker toggle button is visible and interactive
    const pickerToggle = page.locator('mat-datepicker-toggle button');
    await expect(pickerToggle).toBeVisible();

    // Open date picker popup and verify calendar overlay renders
    await pickerToggle.click();
    const calendarPopup = page.locator('mat-calendar').first();
    await expect(calendarPopup).toBeVisible({ timeout: 5000 });

    monitor.assertNoFailures();
  });
});
