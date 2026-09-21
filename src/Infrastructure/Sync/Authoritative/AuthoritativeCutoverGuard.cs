#nullable enable
using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Auth.Infrastructure.Sync.Authoritative
{
    /// <summary>
    /// Permanent post-cutover guard ensuring that once authoritative cutover is committed
    /// (ServerState.CurrentVersion > 0), any Online Daily writes require Sync:AuthoritativeTrackingEnabled = true.
    /// </summary>
    public class AuthoritativeCutoverGuard : IAuthoritativeCutoverGuard
    {
        private readonly IAuthoritativeDatabaseBindingGuard _bindingGuard;

        public AuthoritativeCutoverGuard(IAuthoritativeDatabaseBindingGuard bindingGuard)
        {
            _bindingGuard = bindingGuard ?? throw new ArgumentNullException(nameof(bindingGuard));
        }

        public void ValidateCutoverState(DbContext context, string canonicalDatabaseId)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var normId = canonicalDatabaseId?.Trim();
            if (string.IsNullOrWhiteSpace(normId) || (normId != "2026" && normId != "2027"))
            {
                throw new AuthoritativeCutoverGuardException(
                    $"Authoritative cutover verification failed: invalid or unsupported canonical DatabaseId '{canonicalDatabaseId}'. Expected '2026' or '2027'.",
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }

            if (!context.Database.IsRelational())
            {
                return;
            }

            var connection = context.Database.GetDbConnection();

            try
            {
                _bindingGuard.ValidateAuthoritativeAzureBinding(normId, connection.DataSource, connection.Database);
            }
            catch (Exception ex) when (ex is not AuthoritativeCutoverGuardException)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Authoritative cutover state could not be verified due to physical database binding mismatch.",
                    ex,
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }

            bool openedByUs = false;
            try
            {
                if (connection.State != ConnectionState.Open)
                {
                    connection.Open();
                    openedByUs = true;
                }

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @dbId;";
                var param = cmd.CreateParameter();
                param.ParameterName = "@dbId";
                param.Value = normId;
                cmd.Parameters.Add(param);

                var currentTx = context.Database.CurrentTransaction?.GetDbTransaction();
                if (currentTx != null)
                {
                    cmd.Transaction = currentTx;
                }

                using var reader = cmd.ExecuteReader();
                int rowCount = 0;
                long? currentVersion = null;

                while (reader.Read())
                {
                    rowCount++;
                    if (rowCount == 1 && !reader.IsDBNull(0))
                    {
                        currentVersion = reader.GetInt64(0);
                    }
                }

                EvaluateServerStateRows(rowCount, currentVersion);
            }
            catch (AuthoritativeCutoverGuardException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Authoritative cutover state could not be verified due to database query failure.",
                    ex,
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }
            finally
            {
                if (openedByUs && connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
            }
        }

        public async Task ValidateCutoverStateAsync(DbContext context, string canonicalDatabaseId, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var normId = canonicalDatabaseId?.Trim();
            if (string.IsNullOrWhiteSpace(normId) || (normId != "2026" && normId != "2027"))
            {
                throw new AuthoritativeCutoverGuardException(
                    $"Authoritative cutover verification failed: invalid or unsupported canonical DatabaseId '{canonicalDatabaseId}'. Expected '2026' or '2027'.",
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }

            if (!context.Database.IsRelational())
            {
                return;
            }

            var connection = context.Database.GetDbConnection();

            try
            {
                _bindingGuard.ValidateAuthoritativeAzureBinding(normId, connection.DataSource, connection.Database);
            }
            catch (Exception ex) when (ex is not AuthoritativeCutoverGuardException)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Authoritative cutover state could not be verified due to physical database binding mismatch.",
                    ex,
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }

            bool openedByUs = false;
            try
            {
                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    openedByUs = true;
                }

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @dbId;";
                var param = cmd.CreateParameter();
                param.ParameterName = "@dbId";
                param.Value = normId;
                cmd.Parameters.Add(param);

                var currentTx = context.Database.CurrentTransaction?.GetDbTransaction();
                if (currentTx != null)
                {
                    cmd.Transaction = currentTx;
                }

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                int rowCount = 0;
                long? currentVersion = null;

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rowCount++;
                    if (rowCount == 1 && !reader.IsDBNull(0))
                    {
                        currentVersion = reader.GetInt64(0);
                    }
                }

                EvaluateServerStateRows(rowCount, currentVersion);
            }
            catch (AuthoritativeCutoverGuardException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Authoritative cutover state could not be verified due to database query failure.",
                    ex,
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }
            finally
            {
                if (openedByUs && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }
        }

        private static void EvaluateServerStateRows(int rowCount, long? currentVersion)
        {
            if (rowCount == 0)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Authoritative cutover state could not be verified: ServerState record is missing.",
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }

            if (rowCount > 1)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Authoritative cutover state could not be verified: duplicate ServerState records detected.",
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }

            if (!currentVersion.HasValue || currentVersion.Value < 0)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Authoritative cutover state could not be verified: ServerState CurrentVersion is null or invalid.",
                    AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable);
            }

            if (currentVersion.Value > 0)
            {
                throw new AuthoritativeCutoverGuardException(
                    "Online Daily mutations are rejected: authoritative tracking cutover is already committed (ServerVersion > 0), but Sync:AuthoritativeTrackingEnabled is false.",
                    AuthoritativeCutoverErrorCodes.CutoverCommittedTrackingDisabled);
            }

            // currentVersion == 0: pre-cutover database, allowed.
        }
    }
}
