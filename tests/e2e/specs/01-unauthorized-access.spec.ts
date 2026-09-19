import { test, expect } from '@playwright/test';

test.describe('Unauthorized Navigation Protection', () => {
  const protectedRoutes = [
    { name: 'Root / Dashboard', path: '/' },
    { name: 'Employee List', path: '/employee/list' },
    { name: 'Daily Management', path: '/daily' },
    { name: 'Department Management', path: '/department' },
    { name: 'Watchlist', path: '/watchlist' }
  ];

  for (const route of protectedRoutes) {
    test(`Unauthenticated request to ${route.name} (${route.path}) blocks and redirects to login`, async ({ page }) => {
      // Ensure no previous credentials in localStorage
      await page.goto('/account/login');
      await page.evaluate(() => {
        localStorage.clear();
        sessionStorage.clear();
      });

      // Attempt navigation to protected route
      await page.goto(route.path);
      await page.waitForLoadState('networkidle');

      // Verify redirected to /account/login
      expect(page.url()).toContain('/account/login');

      // Verify login form is visible
      const loginForm = page.locator('form');
      await expect(loginForm).toBeVisible();
    });
  }
});
