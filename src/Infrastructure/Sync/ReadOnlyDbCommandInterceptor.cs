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
    /// (INSERT, UPDATE, DELETE, ALTER, DROP, TRUNCATE, MERGE) when ReadOnlyMode is active.
    /// </summary>
    public class ReadOnlyDbCommandInterceptor : DbCommandInterceptor
    {
        private static readonly Regex MutatingSqlPattern = new Regex(
            @"\b(INSERT\s+INTO|UPDATE\s+|DELETE\s+FROM|DELETE\s+|MERGE\s+|ALTER\s+TABLE|DROP\s+TABLE|TRUNCATE\s+TABLE|CREATE\s+TABLE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ISyncConnectionProvider _syncConnectionProvider;

        public ReadOnlyDbCommandInterceptor(ISyncConnectionProvider syncConnectionProvider)
        {
            _syncConnectionProvider = syncConnectionProvider;
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

            if (MutatingSqlPattern.IsMatch(command.CommandText))
            {
                var snippet = command.CommandText.Trim();
                if (snippet.Length > 60)
                {
                    snippet = snippet.Substring(0, 60) + "...";
                }

                throw new ReadOnlyModeException(
                    $"النظام يعمل حالياً في وضع القراءة المحلية فقط. تنفيذ أوامر التعديل على قاعدة البيانات معطل: [{snippet}]");
            }
        }
    }
}
