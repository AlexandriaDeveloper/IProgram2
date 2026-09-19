import { Page, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

// Automatically load local .env if present
function loadEnvFile() {
  const envLocations = [
    path.resolve(__dirname, '../.env'),
    path.resolve(__dirname, '../../.env'),
  ];
  for (const envPath of envLocations) {
    if (fs.existsSync(envPath)) {
      const content = fs.readFileSync(envPath, 'utf-8');
      for (const line of content.split('\n')) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        const idx = trimmed.indexOf('=');
        if (idx !== -1) {
          const key = trimmed.slice(0, idx).trim();
          const val = trimmed.slice(idx + 1).trim().replace(/^["']|["']$/g, '');
          if (!process.env[key]) {
            process.env[key] = val;
          }
        }
      }
      break;
    }
  }
}

loadEnvFile();

/**
 * Returns validated credentials strictly from environment variables.
 * Fails fast with a sanitized message if missing. Never logs or leaks credential values.
 */
export function getTestCredentials() {
  const username = process.env.E2E_USERNAME;
  const password = process.env.E2E_PASSWORD;

  if (!username || !password) {
    throw new Error(
      'Missing required E2E credentials: E2E_USERNAME and E2E_PASSWORD must be configured ' +
      'in environment variables or in tests/e2e/.env (see tests/e2e/.env.example).'
    );
  }

  return { username, password };
}

/**
 * Validates JWT token claim 'db' matches the canonical expected year
 * without printing or storing the token in evidence.
 */
export function verifyJwtDbClaim(token: string, expectedYear: string) {
  expect(token).toBeTruthy();
  const parts = token.split('.');
  expect(parts.length).toBe(3);

  // Decode JWT payload (standard base64url)
  const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
  const jsonPayload = Buffer.from(base64, 'base64').toString('utf-8');
  const payload = JSON.parse(jsonPayload);

  expect(payload.db).toBe(expectedYear);
}

export interface ApiMonitorOptions {
  allowedErrors?: Array<{ status: number; pathSubstring: string }>;
}

/**
 * Reusable network monitor that tracks all /api/** responses.
 * Fails test on any unexpected HTTP status >= 400.
 */
export function attachApiMonitor(page: Page, options?: ApiMonitorOptions) {
  const unexpectedErrors: { status: number; url: string }[] = [];
  const observedEndpoints = new Set<string>();

  page.on('response', response => {
    const url = response.url();
    if (url.includes('/api/')) {
      const status = response.status();
      try {
        const parsed = new URL(url);
        observedEndpoints.add(`${response.request().method()} ${parsed.pathname}`);
      } catch {
        observedEndpoints.add(`${response.request().method()} ${url}`);
      }

      if (status >= 400) {
        const isAllowed = options?.allowedErrors?.some(
          e => e.status === status && url.includes(e.pathSubstring)
        );
        if (!isAllowed) {
          unexpectedErrors.push({ status, url });
        }
      }
    }
  });

  return {
    assertNoFailures: () => {
      if (unexpectedErrors.length > 0) {
        const details = unexpectedErrors.map(e => `${e.status} ${e.url}`).join('; ');
        throw new Error(`Unexpected API error response(s) detected: ${details}`);
      }
    },
    getObservedEndpoints: () => Array.from(observedEndpoints),
  };
}

/**
 * Performs browser-level login through the Angular UI, verifying the API response and claims.
 */
export async function loginThroughUI(page: Page, year: string = '2026') {
  const creds = getTestCredentials();
  const monitor = attachApiMonitor(page);

  await page.goto('/account/login');
  await page.waitForLoadState('networkidle');

  // Fill credentials
  const usernameInput = page.locator('input[formControlName="username"]');
  const passwordInput = page.locator('input[formControlName="password"]');

  await usernameInput.fill(creds.username);
  await passwordInput.fill(creds.password);

  // Select database year if needed
  const dbSelect = page.locator('mat-select[formControlName="database"]');
  await expect(dbSelect).toBeVisible({ timeout: 10000 });
  
  const currentText = await dbSelect.innerText();
  if (!currentText.includes(year)) {
    await dbSelect.click();
    const option = page.locator(`mat-option:has-text("${year}")`);
    await option.waitFor({ state: 'visible', timeout: 5000 });
    await option.click();
    await page.locator('.cdk-overlay-backdrop').waitFor({ state: 'hidden', timeout: 5000 }).catch(() => {});
  }

  // Intercept the login API request
  const loginResponsePromise = page.waitForResponse(
    resp => resp.url().includes('/api/account/login') && resp.request().method() === 'POST',
    { timeout: 15000 }
  );

  // Click login submit button
  const submitButton = page.locator('button[type="submit"]');
  await submitButton.click();

  const loginResponse = await loginResponsePromise;
  expect(loginResponse.status()).toBe(200);

  const responseJson = await loginResponse.json();
  expect(responseJson.token).toBeTruthy();
  verifyJwtDbClaim(responseJson.token, year);

  // Wait for redirect away from login
  await page.waitForURL((url) => !url.pathname.includes('/account/login'), { timeout: 20000 });
  await page.waitForLoadState('networkidle');

  // Verify Angular stored the selection in localStorage under 'db-selection'
  const storedDb = await page.evaluate(() => localStorage.getItem('db-selection'));
  expect(storedDb).toBe(year);

  monitor.assertNoFailures();
}
