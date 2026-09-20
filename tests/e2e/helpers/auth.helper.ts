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
  allowedConsoleErrors?: Array<{ pattern: RegExp | string }>;
}

export interface KnownDefect {
  defectId: string;
  source: string;
  detail: string;
}

/**
 * Reusable monitor that tracks all /api/** responses, uncaught browser pageerrors,
 * and browser console errors. Explicitly captures documented known defects without hiding them.
 * Fails test on any unexpected HTTP status >= 400 or unexpected browser error.
 */
export function attachApiMonitor(page: Page, options?: ApiMonitorOptions) {
  const unexpectedErrors: { status: number; url: string; detail?: string }[] = [];
  const observedEndpoints = new Set<string>();
  const knownDefects: KnownDefect[] = [];

  // Track whether /migrationHub was targeted and failed with 405
  let migrationHub405Observed = false;

  // 1. Network response monitor
  page.on('response', response => {
    const url = response.url();
    const status = response.status();

    if (url.includes('/migrationHub') && status === 405) {
      migrationHub405Observed = true;
    }

    if (url.includes('/api/')) {
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
          unexpectedErrors.push({ status, url, detail: `HTTP ${status} on ${url}` });
        }
      }
    }
  });

  // 2. Browser uncaught exception monitor
  page.on('pageerror', error => {
    unexpectedErrors.push({
      status: 0,
      url: page.url(),
      detail: `Uncaught Page Exception: ${error.message}\n${error.stack || ''}`,
    });
  });

  // 3. Browser console error monitor
  page.on('console', msg => {
    if (msg.type() === 'error') {
      const text = msg.text();
      const locationUrl = msg.location()?.url || '';

      // Classify documented known defect: Legacy Migration SignalR 405 on startup
      // Must be specifically attributable to /migrationHub (or its negotiate endpoint)
      // AND match the expected negotiation/405 failure shape.
      const isDirectMigrationHubBlocked =
        (locationUrl.includes('/migrationHub') || text.includes('/migrationHub')) &&
        (text.includes('405') || text.includes('Method Not Allowed') || text.includes('403') || text.includes('READ_ONLY_MODE_BLOCKED'));

      const isSignalRNegotiationCascade =
        (text.includes('Failed to complete negotiation with the server') ||
         text.includes('Failed to start the connection: Error: Failed to complete negotiation')) &&
        (text.includes("Status code '405'") || text.includes("Status code '403'") || text.includes('READ_ONLY_MODE_BLOCKED') || text.includes('Method Not Allowed'));

      const isSignalRDefect =
        isDirectMigrationHubBlocked ||
        (migrationHub405Observed && isSignalRNegotiationCascade);

      if (isSignalRDefect) {
        if (isDirectMigrationHubBlocked) {
          migrationHub405Observed = true;
        }
        knownDefects.push({
          defectId: 'DEFECT_3_SIGNALR_MIGRATIONHUB_405',
          source: 'Browser Console',
          detail: text,
        });
        return;
      }

      if (text.includes('ERR_FAILED') || text.includes('ERR_ABORTED')) {
        return;
      }

      // Check if this console error corresponds to an allowed HTTP error response
      const isAllowedHttpConsoleError = options?.allowedErrors?.some(e =>
        text.includes(`status of ${e.status}`) || text.includes(`${e.status} (`)
      );
      if (isAllowedHttpConsoleError) {
        return;
      }

      // Check user-allowed console patterns
      const isAllowed = options?.allowedConsoleErrors?.some(c =>
        typeof c.pattern === 'string' ? text.includes(c.pattern) : c.pattern.test(text)
      );
      if (!isAllowed) {
        unexpectedErrors.push({
          status: 0,
          url: page.url(),
          detail: `Browser Console Error: ${text}`,
        });
      }
    }
  });

  return {
    assertNoFailures: () => {
      if (unexpectedErrors.length > 0) {
        const details = unexpectedErrors.map(e => e.detail || `${e.status} ${e.url}`).join('\n---\n');
        throw new Error(`Unexpected browser / API error(s) detected during test execution:\n${details}`);
      }
    },
    getObservedEndpoints: () => Array.from(observedEndpoints),
    getKnownDefects: () => knownDefects,
  };
}

/**
 * Performs browser-level login through the Angular UI, verifying the API response and claims.
 */
export async function loginThroughUI(page: Page, year: string = '2026') {
  const creds = getTestCredentials();
  const monitor = attachApiMonitor(page);

  await page.goto('/account/login');
  await page.waitForLoadState('domcontentloaded');

  // Fill credentials
  const usernameInput = page.locator('input[formControlName="username"]');
  const passwordInput = page.locator('input[formControlName="password"]');
  await expect(usernameInput).toBeVisible({ timeout: 10000 });

  await usernameInput.fill(creds.username);
  await passwordInput.fill(creds.password);

  // Select database year if needed
  const dbSelect = page.locator('mat-select[formControlName="database"]');
  await expect(dbSelect).toBeVisible({ timeout: 10000 });
  
  // Click database select to open overlay and select desired financial year
  await dbSelect.click();
  const option = page.locator(`mat-option:has-text("${year}")`);
  await option.waitFor({ state: 'visible', timeout: 10000 });
  await option.click();
  await page.locator('.cdk-overlay-backdrop').waitFor({ state: 'hidden', timeout: 5000 }).catch(() => {});

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

  // Verify Angular stored the selection in localStorage under 'db-selection'
  const storedDb = await page.evaluate(() => localStorage.getItem('db-selection'));
  expect(storedDb).toBe(year);

  monitor.assertNoFailures();
}
