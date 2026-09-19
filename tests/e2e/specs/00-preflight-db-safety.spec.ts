import { test, expect } from '@playwright/test';
import { getTestCredentials } from '../helpers/auth.helper';

test.describe('E2E Preflight Safety & Environment Validation', () => {

  test('Required environment variables are configured without hardcoded fallbacks', async () => {
    const creds = getTestCredentials();
    expect(creds.username).toBeTruthy();
    expect(creds.password).toBeTruthy();
    // Validate credentials are non-empty strings and not default placeholders
    expect(creds.username).not.toBe('your_username_here');
    expect(creds.password).not.toBe('your_password_here');
  });

  test('Runtime database connection strings resolve strictly to approved local databases (Zero Azure / Zero Quarantine)', async ({ request }) => {
    const response = await request.get('/api/diagnostics/e2e-db-safety');
    expect(response.status()).toBe(200);

    const body = await response.json();
    expect(body.safetyCheckPassed).toBe(true);
    expect(body.databaseCount).toBeGreaterThanOrEqual(2);

    for (const db of body.databases) {
      // Must be local SQL Server instance
      expect(db.serverClassification).toBe('LOCAL_INSTANCE');
      // Must NOT be remote or Azure SQL
      expect(db.isAzureOrRemote).toBe(false);
      // Must NOT be the quarantined database IProgramLocalDb2026
      expect(db.isQuarantinedDatabase).toBe(false);
      // Must be an approved development database (IProgramDb2026 or IProgramDb2027)
      expect(db.approvedDatabase).toBe(true);
      expect(db.isSafe).toBe(true);
    }
  });
});
