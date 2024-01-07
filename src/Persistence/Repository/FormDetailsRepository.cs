using System.Security.Claims;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Repository
{
    public class FormDetailsRepository : GenericRepository<FormDetails>, IFormDetailsRepository
    {
        private readonly ApplicationContext _context;
        private readonly IHttpContextAccessor _accessor;
        public FormDetailsRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this._accessor = accessor;
            this._context = context;
        }


        public async Task<FormDetails> AddEmployeeToFormDetails(FormDetails form)
        {


            form.CreatedBy = _accessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;
            form.CreatedAt = DateTime.Now;
            await _context.Set<FormDetails>().AddAsync(form);

            return form;

        }
        public int GetMaxOrderNum(int formId)
        {

            var max = _context.Set<FormDetails>()
            .Where(x => x.FormId == formId && x.IsActive == true)
            .Max(e => (int?)e.OrderNum);
            return max.HasValue ? max.Value : 0;
        }

        public async Task<bool> CheckEmployeeFormDetailsExist(int employeeId, int formId)
        {
            return await _context.Set<FormDetails>().Include(x => x.Employee)
            .AnyAsync(x => x.EmployeeId == employeeId && x.FormId == formId && x.IsActive == true)
           ;
        }
    }
}