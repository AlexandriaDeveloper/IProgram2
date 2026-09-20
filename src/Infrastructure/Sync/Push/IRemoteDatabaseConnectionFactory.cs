#nullable enable
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Auth.Infrastructure.Sync.Push
{
    public interface IRemoteDatabaseConnectionFactory
    {
        Task<DbConnection> CreateOpenConnectionAsync(string databaseId, CancellationToken cancellationToken);
    }
}
