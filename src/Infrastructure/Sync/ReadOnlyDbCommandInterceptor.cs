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
        private readonly ISyncConnectionProvider _syncConnectionProvider;

        public ReadOnlyDbCommandInterceptor(ISyncConnectionProvider syncConnectionProvider)
        {
            _syncConnectionProvider = syncConnectionProvider;
        }

        public static bool IsMutatingCommand(string commandText, System.Data.CommandType commandType = System.Data.CommandType.Text)
        {
            return !IsReadOnlyPermitted(commandText, commandType);
        }

        public static bool IsMutatingCommand(DbCommand command)
        {
            if (command == null) return false;
            return !IsReadOnlyPermitted(command.CommandText, command.CommandType);
        }

        public static bool IsReadOnlyPermitted(string sql, System.Data.CommandType commandType = System.Data.CommandType.Text)
        {
            // 1. Fail-closed on StoredProcedure: stored procedures can execute hidden DML/DDL mutations
            if (commandType == System.Data.CommandType.StoredProcedure)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                return true;
            }

            // 2. Parse using Microsoft's official TSql120Parser (SQL Server 2014)
            var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql120Parser(initialQuotedIdentifiers: true);
            using var reader = new System.IO.StringReader(sql);
            var fragment = parser.Parse(reader, out var errors);

            // 3. Fail-closed on syntax/parse errors
            if (errors != null && errors.Count > 0)
            {
                return false;
            }

            if (fragment is not Microsoft.SqlServer.TransactSql.ScriptDom.TSqlScript script)
            {
                return false;
            }

            // 4. Validate every statement across every batch against an explicit allowlist (default-deny)
            if (script.Batches == null || script.Batches.Count == 0)
            {
                return true;
            }

            foreach (var batch in script.Batches)
            {
                if (batch.Statements == null) continue;

                foreach (var stmt in batch.Statements)
                {
                    if (!IsStatementPermittedReadOnly(stmt))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsStatementPermittedReadOnly(Microsoft.SqlServer.TransactSql.ScriptDom.TSqlStatement stmt)
        {
            switch (stmt)
            {
                case Microsoft.SqlServer.TransactSql.ScriptDom.SelectStatement selectStmt:
                    if (selectStmt.Into != null)
                    {
                        return false;
                    }
                    return true;

                case Microsoft.SqlServer.TransactSql.ScriptDom.PredicateSetStatement:
                case Microsoft.SqlServer.TransactSql.ScriptDom.SetTransactionIsolationLevelStatement:
                case Microsoft.SqlServer.TransactSql.ScriptDom.SetVariableStatement:
                case Microsoft.SqlServer.TransactSql.ScriptDom.SetCommandStatement:
                case Microsoft.SqlServer.TransactSql.ScriptDom.UseStatement:
                    return true;

                default:
                    // All other statements (UpdateStatement, InsertStatement, DeleteStatement,
                    // MergeStatement, ExecuteStatement, AlterTableStatement, DropTableStatement,
                    // TruncateTableStatement, CreateTableStatement, etc.) are strictly DENIED.
                    return false;
            }
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

            if (command == null)
            {
                return;
            }

            if (IsMutatingCommand(command))
            {
                throw new ReadOnlyModeException(
                    "النظام يعمل حالياً في وضع القراءة المحلية فقط. تنفيذ أوامر التعديل على قاعدة البيانات معطل.");
            }
        }
    }
}
