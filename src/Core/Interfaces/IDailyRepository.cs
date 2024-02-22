using Core.Models;

namespace Core.Interfaces
{
    public interface IDailyRepository : IGenericRepository<Daily>
    {
        bool IsClosed(int id);
    }
}