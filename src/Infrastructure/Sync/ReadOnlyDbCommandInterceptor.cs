using System;
using System.Data.Common;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.Infrastructure.Sync
{
    /// <summary>
    /// EF Core DbCommandInterceptor that prevents any raw SQL or command-level mutations
    /// (INSERT, UPDATE, DELETE, ALTER, DROP, TRUNCATE, MERGE, SELECT INTO, EXEC) when ReadOnlyMode is active.
    /// Intercepts NonQuery, Reader, and Scalar (Sync and Async) executions.
    /// </summary>
    public class ReadOnlyDbCommandInterceptor : DbCommandInterceptor
    {
        private static readonly Regex MultiLineComments = new Regex(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
        private static readonly Regex SingleLineComments = new Regex(@"--[^\r\n]*", RegexOptions.Compiled);
        private static readonly Regex StringLiterals = new Regex(@"N?'([^']|'')*'", RegexOptions.Compiled);

        private static readonly Regex MutatingSqlPattern = new Regex(
            @"\b(INSERT|UPDATE|DELETE|MERGE|ALTER|DROP|TRUNCATE|CREATE|EXEC|EXECUTE)\b|\bSELECT\b[\s\S]+?\bINTO\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ISyncConnectionProvider _syncConnectionProvider;

        public ReadOnlyDbCommandInterceptor(ISyncConnectionProvider syncConnectionProvider)
        {
            _syncConnectionProvider = syncConnectionProvider;
        }

        public static string SanitizeSqlForAnalysis(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
            var withoutComments = MultiLineComments.Replace(sql, " ");
            withoutComments = SingleLineComments.Replace(withoutComments, " ");
            var withoutLiterals = StringLiterals.Replace(withoutComments, "''");
            return withoutLiterals;
        }

        public static bool IsMutatingCommand(string commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText)) return false;
            var sanitized = SanitizeSqlForAnalysis(commandText);
            return MutatingSqlPattern.IsMatch(sanitized);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            EnforceReadOnlyCommand(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            EnforceReadOnlyCommand(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            EnforceReadOnlyCommand(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            EnforceReadOnlyCommand(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            EnforceReadOnlyCommand(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            EnforceReadOnlyCommand(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void EnforceReadOnlyCommand(DbCommand command)
        {
            if (_syncConnectionProvider == null || !_syncConnectionProvider.IsReadOnlyMode)
            {
                return;
            }

            if (command == null || string.IsNullOrWhiteSpace(command.CommandText))
            {
                return;
            }

            if (IsMutatingCommand(command.CommandText))
            {
                throw new ReadOnlyModeException(
                    "النظام يعمل حالياً في وضع القراءة المحلية فقط. تنفيذ أوامر التعديل على قاعدة البيانات معطل.");
            }
        }
    }
}
