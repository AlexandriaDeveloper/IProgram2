import { test, expect } from '@playwright/test';
import { loginThroughUI, verifyJwtDbClaim, attachApiMonitor, getTestCredentials } from '../helpers/auth.helper';

test.describe('Offline Read-Only Runtime E2E Verification', () => {

  test.beforeEach(async ({ page }) => {
    // Stub heavy background video streams to 200 OK empty to prevent network starvation and console errors
    await page.route('**/*.{mov,mp4}', route => route.fulfill({ status: 200, contentType: 'video/mp4', body: '' }));
  });

  test('Navbar renders prominent Read-Only Indicator badge on login page', async ({ page }) => {
    const monitor = attachApiMonitor(page);
    await page.goto('/account/login');
    await page.waitForLoadState('domcontentloaded');

    // Verify Read-Only badge is visible and displays correct text
    const badge = page.locator('.readonly-badge');
    await expect(badge).toBeVisible({ timeout: 10000 });
    await expect(badge).toContainText('🔒 محلي — قراءة فقط');

    monitor.assertNoFailures();
  });

  test('Year 2026: UI Login succeeds, maintains Read-Only badge, and displays dashboard metrics', async ({ page }) => {
    const monitor = await loginThroughUI(page, '2026');

    // 1. Verify redirected to dashboard and token belongs to 2026
    const token = await page.evaluate(() => localStorage.getItem('token'));
    expect(token).toBeTruthy();
    verifyJwtDbClaim(token!, '2026');

    // 2. Verify Read-Only badge is visible in authenticated navbar
    const badge = page.locator('.readonly-badge');
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('🔒 محلي — قراءة فقط');

    // 3. Verify sync buttons are suppressed/hidden in read-only mode
    const pushBtn = page.locator('.sync-btn.push-btn');
    const pullBtn = page.locator('.sync-btn.pull-btn');
    await expect(pushBtn).toHaveCount(0);
    await expect(pullBtn).toHaveCount(0);

    // 4. Verify actual Dashboard elements and metrics rendered on screen
    const dashboardTitle = page.locator('h2:has-text("لوحة المعلومات")');
    await expect(dashboardTitle).toBeVisible({ timeout: 15000 });

    const kpiCard = page.locator('.kpi-card').first();
    await expect(kpiCard).toBeVisible({ timeout: 10000 });

    const empLabel = page.locator('.kpi-label:has-text("الموظفين")');
    await expect(empLabel).toBeVisible();

    const empValText = await page.locator('.kpi-value').first().textContent();
    expect(parseInt(empValText?.trim() || '0', 10)).toBeGreaterThan(0);

    monitor.assertNoFailures();
  });

  test('Year 2027: UI Login succeeds with distinct 2027 data isolation and dashboard rendering', async ({ page }) => {
    const monitor = await loginThroughUI(page, '2027');

    const token = await page.evaluate(() => localStorage.getItem('token'));
    expect(token).toBeTruthy();
    verifyJwtDbClaim(token!, '2027');

    const badge = page.locator('.readonly-badge');
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('🔒 محلي — قراءة فقط');

    // Verify dashboard renders for 2027
    const dashboardTitle = page.locator('h2:has-text("لوحة المعلومات")');
    await expect(dashboardTitle).toBeVisible({ timeout: 15000 });

    const kpiCard = page.locator('.kpi-card').first();
    await expect(kpiCard).toBeVisible({ timeout: 10000 });

    monitor.assertNoFailures();
  });

  test('Offline Read: Employee search, Daily batches, and Department reports execute successfully', async ({ page, request }) => {
    const monitor = await loginThroughUI(page, '2026');
    const token = await page.evaluate(() => localStorage.getItem('token'));
    const headers = {
      'Authorization': `Bearer ${token}`,
      'X-Db-Selection': '2026'
    };

    // 1. Employee query & search
    const empResp = await request.get('/api/Employee/GetEmployees?pageIndex=1&pageSize=10', { headers });
    expect(empResp.status()).toBe(200);
    const empData = await empResp.json();
    expect(empData.data).toBeTruthy();
    expect(empData.count).toBeGreaterThan(0);

    // 2. Daily batches query
    const dailyResp = await request.get('/api/Daily', { headers });
    expect(dailyResp.status()).toBe(200);
    const dailyData = await dailyResp.json();
    const dailyList = Array.isArray(dailyData) ? dailyData : (dailyData.data || []);
    expect(Array.isArray(dailyList)).toBe(true);

    // 3. Departments master query
    const deptResp = await request.get('/api/Department', { headers });
    expect(deptResp.status()).toBe(200);
    const deptData = await deptResp.json();
    const deptList = Array.isArray(deptData) ? deptData : (deptData.data || []);
    expect(Array.isArray(deptList)).toBe(true);
    expect(deptList.length).toBeGreaterThan(0);

    monitor.assertNoFailures();
  });

  test('Excel Form Export returns 200 OK with binary spreadsheet content', async ({ page, request }) => {
    const monitor = await loginThroughUI(page, '2026');
    const token = await page.evaluate(() => localStorage.getItem('token'));

    const response = await request.post('/api/Form/download-form', {
      headers: {
        'Authorization': `Bearer ${token}`,
        'X-Db-Selection': '2026',
        'Content-Type': 'application/json'
      },
      data: { formId: 1, formTitle: 'E2E_Test_Export' }
    });

    expect(response.status()).toBe(200);
    const buffer = await response.body();
    expect(buffer.length).toBeGreaterThan(1000);

    monitor.assertNoFailures();
  });

  test('Mutating operations are rejected with 403 Forbidden across independent 2026 and 2027 sessions', async ({ request }) => {
    const creds = getTestCredentials();

    // === Year 2026 Mutation Matrix ===
    // 1. Authenticate independently for Year 2026
    const login2026Resp = await request.post('/api/account/login', {
      headers: { 'Content-Type': 'application/json', 'X-Db-Selection': '2026' },
      data: { username: creds.username, password: creds.password }
    });
    expect(login2026Resp.status()).toBe(200);
    const token2026 = (await login2026Resp.json()).token;
    expect(token2026).toBeTruthy();
    verifyJwtDbClaim(token2026, '2026');

    const headers2026 = {
      'Authorization': `Bearer ${token2026}`,
      'X-Db-Selection': '2026',
      'Content-Type': 'application/json'
    };

    const mutationCases2026: { name: string; method: string; url: string; data?: any }[] = [
      { name: 'PostDaily', method: 'POST', url: '/api/Daily', data: { dayDate: '2026-05-01', departmentId: 1, notes: 'mutation' } },
      { name: 'PutDaily', method: 'PUT', url: '/api/Daily/1', data: { id: 1, dayDate: '2026-05-01', departmentId: 1 } },
      { name: 'DeleteDaily', method: 'DELETE', url: '/api/Daily/999999' },
      { name: 'CopyArchive', method: 'GET', url: '/api/Form/CopyFormToArchive/1' },
      { name: 'MarkReviewed', method: 'PUT', url: '/api/formDetails/markAsReviewed/1', data: true },
      { name: 'MarkSummaryReviewed', method: 'PUT', url: '/api/formDetails/markAsSummaryReviewed/1', data: true },
      { name: 'RegisterUser', method: 'POST', url: '/api/account/register', data: { username: 'unauth', email: 'unauth@test.com', password: 'Password123!' } },
      { name: 'CreateRole', method: 'POST', url: '/api/role/createRole', data: { roleName: 'UnauthRole' } },
      { name: 'DeleteReference', method: 'DELETE', url: '/api/formReferences/DeleteFormReference/1' },
      { name: 'PostEmployee', method: 'POST', url: '/api/Employees', data: { name: 'unauth' } }
    ];

    for (const mCase of mutationCases2026) {
      let resp;
      if (mCase.method === 'POST') {
        resp = await request.post(mCase.url, { headers: headers2026, data: mCase.data });
      } else if (mCase.method === 'PUT') {
        resp = await request.put(mCase.url, { headers: headers2026, data: mCase.data });
      } else if (mCase.method === 'DELETE') {
        resp = await request.delete(mCase.url, { headers: headers2026 });
      } else {
        resp = await request.get(mCase.url, { headers: headers2026 });
      }
      expect(resp.status(), `Year 2026: ${mCase.name} should be rejected with 403`).toBe(403);
      const json = await resp.json().catch(() => ({}));
      expect(json.code).toBe('READ_ONLY_MODE_BLOCKED');
    }

    // === Year 2027 Mutation Matrix ===
    // 2. Authenticate independently for Year 2027 with its own token
    const login2027Resp = await request.post('/api/account/login', {
      headers: { 'Content-Type': 'application/json', 'X-Db-Selection': '2027' },
      data: { username: creds.username, password: creds.password }
    });
    expect(login2027Resp.status()).toBe(200);
    const token2027 = (await login2027Resp.json()).token;
    expect(token2027).toBeTruthy();
    verifyJwtDbClaim(token2027, '2027');

    const headers2027 = {
      'Authorization': `Bearer ${token2027}`,
      'X-Db-Selection': '2027',
      'Content-Type': 'application/json'
    };

    const mutationCases2027: { name: string; method: string; url: string; data?: any }[] = [
      { name: 'PostDaily', method: 'POST', url: '/api/Daily', data: { dayDate: '2027-05-01', departmentId: 1, notes: 'mutation 2027' } },
      { name: 'PutDaily', method: 'PUT', url: '/api/Daily/1', data: { id: 1, dayDate: '2027-05-01', departmentId: 1 } },
      { name: 'DeleteDaily', method: 'DELETE', url: '/api/Daily/999999' },
      { name: 'CopyArchive', method: 'GET', url: '/api/Form/CopyFormToArchive/1' },
      { name: 'MarkReviewed', method: 'PUT', url: '/api/formDetails/markAsReviewed/1', data: true },
      { name: 'MarkSummaryReviewed', method: 'PUT', url: '/api/formDetails/markAsSummaryReviewed/1', data: true },
      { name: 'RegisterUser', method: 'POST', url: '/api/account/register', data: { username: 'unauth27', email: 'unauth27@test.com', password: 'Password123!' } },
      { name: 'CreateRole', method: 'POST', url: '/api/role/createRole', data: { roleName: 'UnauthRole27' } },
      { name: 'DeleteReference', method: 'DELETE', url: '/api/formReferences/DeleteFormReference/1' },
      { name: 'PostEmployee', method: 'POST', url: '/api/Employees', data: { name: 'unauth27' } }
    ];

    for (const mCase of mutationCases2027) {
      let resp;
      if (mCase.method === 'POST') {
        resp = await request.post(mCase.url, { headers: headers2027, data: mCase.data });
      } else if (mCase.method === 'PUT') {
        resp = await request.put(mCase.url, { headers: headers2027, data: mCase.data });
      } else if (mCase.method === 'DELETE') {
        resp = await request.delete(mCase.url, { headers: headers2027 });
      } else {
        resp = await request.get(mCase.url, { headers: headers2027 });
      }
      expect(resp.status(), `Year 2027: ${mCase.name} should be rejected with 403`).toBe(403);
      const json = await resp.json().catch(() => ({}));
      expect(json.code).toBe('READ_ONLY_MODE_BLOCKED');
    }
  });
});
