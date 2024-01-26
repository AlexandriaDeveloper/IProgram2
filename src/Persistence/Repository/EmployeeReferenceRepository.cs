using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class EmployeeReferenceRepository : GenericRepository<EmployeeRefernce>, IEmployeeRefernceRepository
    {
        private readonly ApplicationContext _context;


        // private readonly ApplicationContext _context;

        public EmployeeReferenceRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this._context = context;

        }


    }
}