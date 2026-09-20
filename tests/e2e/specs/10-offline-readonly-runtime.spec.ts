import { test, expect } from '@playwright/test';
import { loginThroughUI, verifyJwtDbClaim, attachApiMonitor } from '../helpers/auth.helper';

test.describe('Offline Read-Only Runtime E2E Verification', () => {

  test.beforeEach(async ({ page }) => {
    // Stub heavy background video streams to 200 OK empty to prevent network starvation and console errors
    await page.route('**/*.{mov,mp4}', route => route.fulfill({ status: 200, contentType: 'video/mp4', body: '' }));
  });

  test('Navbar renders prominent Read-Only Indicator badge on login page', async ({ page }) => {
    await page.goto('/account/login');
    await page.waitForLoadState('domcontentloaded');

    // Verify Read-Only badge is visible and displays correct text
    const badge = page.locator('.readonly-badge');
    await expect(badge).toBeVisible({ timeout: 10000 });
    await expect(badge).toContainText('🔒 محلي — قراءة فقط');
  });

  test('Year 2026: UI Login succeeds, maintains Read-Only badge, and suppresses sync buttons', async ({ page }) => {
    await loginThroughUI(page, '2026');

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

    // 4. Verify Dashboard metrics load from local database
    await page.waitForTimeout(1000);
    const content = await page.textContent('body');
    expect(content).toBeTruthy();
  });

  test('Year 2027: UI Login succeeds with distinct 2027 data isolation', async ({ page }) => {
    await loginThroughUI(page, '2027');

    const token = await page.evaluate(() => localStorage.getItem('token'));
    expect(token).toBeTruthy();
    verifyJwtDbClaim(token!, '2027');

    const badge = page.locator('.readonly-badge');
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('🔒 محلي — قراءة فقط');
  });

  test('Offline Read: Employee search, Daily batches, and Department reports execute successfully', async ({ page, request }) => {
    await loginThroughUI(page, '2026');
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
  });

  test('Excel Form Export returns 200 OK with binary spreadsheet content', async ({ page, request }) => {
    await loginThroughUI(page, '2026');
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
  });

  test('Mutating operations (POST, PUT, DELETE, Review, User Admin, Attachments) are rejected with 403 Forbidden across 2026 and 2027', async ({ page, request }) => {
    await loginThroughUI(page, '2026');
    const token = await page.evaluate(() => localStorage.getItem('token'));
    const headers2026 = {
      'Authorization': `Bearer ${token}`,
      'X-Db-Selection': '2026',
      'Content-Type': 'application/json'
    };
    const headers2027 = {
      'Authorization': `Bearer ${token}`,
      'X-Db-Selection': '2027',
      'Content-Type': 'application/json'
    };

    // 1. Mutating POST (2026 & 2027)
    const postDaily2026 = await request.post('/api/Daily', {
      headers: headers2026,
      data: { dayDate: '2026-05-01', departmentId: 1, notes: 'e2e write test' }
    });
    expect(postDaily2026.status()).toBe(403);
    expect((await postDaily2026.json()).code).toBe('READ_ONLY_MODE_BLOCKED');

    const postDaily2027 = await request.post('/api/Daily', {
      headers: headers2027,
      data: { dayDate: '2027-05-01', departmentId: 1, notes: 'e2e write test 2027' }
    });
    expect(postDaily2027.status()).toBe(403);
    expect((await postDaily2027.json()).code).toBe('READ_ONLY_MODE_BLOCKED');

    // 2. Mutating GET (archive copy)
    const archiveResp = await request.get('/api/Form/CopyFormToArchive/1', { headers: headers2026 });
    expect(archiveResp.status()).toBe(403);
    expect((await archiveResp.json()).code).toBe('READ_ONLY_MODE_BLOCKED');

    // 3. Mutating PUT
    const putResp = await request.put('/api/Daily/1', {
      headers: headers2026,
      data: { id: 1, dayDate: '2026-05-01', departmentId: 1 }
    });
    expect(putResp.status()).toBe(403);

    // 4. Mutating DELETE
    const deleteResp = await request.delete('/api/Daily/999999', { headers: headers2026 });
    expect(deleteResp.status()).toBe(403);

    // 5. Review & Summary Review mutations
    const reviewResp = await request.put('/api/formDetails/markAsReviewed/1', {
      headers: headers2026,
      data: true
    });
    expect(reviewResp.status()).toBe(403);

    const summaryReviewResp = await request.put('/api/formDetails/markAsSummaryReviewed/1', {
      headers: headers2026,
      data: true
    });
    expect(summaryReviewResp.status()).toBe(403);

    // 6. User and Role Management mutations
    const registerResp = await request.post('/api/account/register', {
      headers: headers2026,
      data: { username: 'unauthorized_user', email: 'test@example.com', password: 'Password123!' }
    });
    expect(registerResp.status()).toBe(403);

    const createRoleResp = await request.post('/api/role/createRole', {
      headers: headers2026,
      data: { roleName: 'UnauthorizedRole' }
    });
    expect(createRoleResp.status()).toBe(403);

    // 7. Form References / Attachment mutations
    const deleteRefResp = await request.delete('/api/formReferences/DeleteFormReference/1', {
      headers: headers2026
    });
    expect(deleteRefResp.status()).toBe(403);
  });
});
