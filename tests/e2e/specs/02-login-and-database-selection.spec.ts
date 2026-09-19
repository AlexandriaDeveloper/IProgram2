import { test, expect } from '@playwright/test';
import { loginThroughUI, verifyJwtDbClaim, attachApiMonitor } from '../helpers/auth.helper';

test.describe('Login Flow and Database/Year Selection', () => {

  test('Database selector renders available configured financial years (2026 & 2027)', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    const dbApiResponsePromise = page.waitForResponse(
      resp => resp.url().includes('/api/account/databases') && resp.status() === 200
    );

    await page.goto('/account/login');
    await page.waitForLoadState('networkidle');

    // Verify databases API call succeeded
    const dbResponse = await dbApiResponsePromise;
    expect(dbResponse.status()).toBe(200);
    const databases = await dbResponse.json();
    expect(databases.some((d: any) => d.id === '2026')).toBe(true);
    expect(databases.some((d: any) => d.id === '2027')).toBe(true);

    // Wait for databases dropdown to be populated
    const dbSelect = page.locator('mat-select[formControlName="database"]');
    await expect(dbSelect).toBeVisible();

    await dbSelect.click();
    await page.waitForTimeout(400);

    // Verify both options are present in the DOM
    const options = page.locator('mat-option');
    const optionTexts = await options.allTextContents();

    expect(optionTexts.some(t => t.includes('2026'))).toBeTruthy();
    expect(optionTexts.some(t => t.includes('2027'))).toBeTruthy();

    await page.keyboard.press('Escape');
    monitor.assertNoFailures();
  });

  test('Successful login with Year 2026 stores token and routes to Dashboard', async ({ page }) => {
    await loginThroughUI(page, '2026');

    // Verify redirected to dashboard root
    expect(page.url()).not.toContain('/account/login');

    // Verify localStorage has token and db-selection
    const token = await page.evaluate(() => localStorage.getItem('token'));
    const dbSelection = await page.evaluate(() => localStorage.getItem('db-selection'));

    expect(token).toBeTruthy();
    expect(token!.length).toBeGreaterThan(50);
    expect(dbSelection).toBe('2026');

    // Verify canonical db claim inside token matches 2026 without leaking token value
    verifyJwtDbClaim(token!, '2026');
  });

  test('Successful login with Year 2027 stores token and routes to Dashboard', async ({ page }) => {
    await loginThroughUI(page, '2027');

    expect(page.url()).not.toContain('/account/login');

    const token = await page.evaluate(() => localStorage.getItem('token'));
    const dbSelection = await page.evaluate(() => localStorage.getItem('db-selection'));

    expect(token).toBeTruthy();
    expect(token!.length).toBeGreaterThan(50);
    expect(dbSelection).toBe('2027');

    // Verify canonical db claim inside token matches 2027 without leaking token value
    verifyJwtDbClaim(token!, '2027');
  });

  test('Login with invalid credentials fails with HTTP 401 and stays on login page', async ({ page }) => {
    const monitor = attachApiMonitor(page, {
      allowedErrors: [{ status: 401, pathSubstring: '/api/account/login' }]
    });

    await page.goto('/account/login');
    await page.waitForLoadState('networkidle');

    await page.locator('input[formControlName="username"]').fill('invalid_user_999');
    await page.locator('input[formControlName="password"]').fill('WrongPassword123!');

    // Intercept login response
    const loginResponsePromise = page.waitForResponse(
      resp => resp.url().includes('/api/account/login') && resp.request().method() === 'POST'
    );

    await page.locator('button[type="submit"]').click();

    const response = await loginResponsePromise;
    // Explicitly assert HTTP 401 Unauthorized
    expect(response.status()).toBe(401);

    await page.waitForTimeout(1000);

    // Verify still on login page and no token was stored
    expect(page.url()).toContain('/account/login');
    const token = await page.evaluate(() => localStorage.getItem('token'));
    expect(token).toBeFalsy();

    monitor.assertNoFailures();
  });
});
