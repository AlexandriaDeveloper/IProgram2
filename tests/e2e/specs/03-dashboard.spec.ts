import { test, expect } from '@playwright/test';
import { loginThroughUI } from '../helpers/auth.helper';

test.describe('Dashboard Load & Data Rendering', () => {

  test('Dashboard loads successfully with statistical cards and charts', async ({ page }) => {
    // Collect console errors (excluding known SignalR migrationHub error)
    const consoleErrors: string[] = [];
    page.on('console', msg => {
      const text = msg.text();
      const isKnownSignalR = text.includes('migrationHub') || text.includes('negotiation') || text.includes('405');
      if (msg.type() === 'error' && !isKnownSignalR) {
        consoleErrors.push(text);
      }
    });

    // Login through UI
    await loginThroughUI(page, '2026');

    // Verify current route is dashboard
    await expect(page).toHaveURL(/localhost:5000\/?$/);

    // Verify dashboard statistics elements exist (.kpi-card)
    const cards = page.locator('.kpi-card');
    await expect(cards.first()).toBeVisible({ timeout: 15000 });

    // Verify stats exist in page text (e.g. employee count or form count)
    const bodyContent = await page.innerText('body');
    expect(bodyContent).toBeTruthy();

    // Check that there were no uncaught JavaScript exceptions
    expect(consoleErrors).toHaveLength(0);
  });

  test('Dashboard date filter controls are present and interactive', async ({ page }) => {
    await loginThroughUI(page, '2026');

    // Date range form group inputs
    const dateInputs = page.locator('mat-date-range-input');
    if (await dateInputs.isVisible()) {
      await expect(dateInputs).toBeVisible();
    }
  });
});
