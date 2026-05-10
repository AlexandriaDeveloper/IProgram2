using Application.Dtos;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Persistence.Helpers;

namespace Application.Features
{
    public class WatchListService
    {
        private readonly IEmployeeWatchListRepository _watchListRepository;
        private readonly IEmployeeRepository _employeeRepository;
        private readonly IUnitOfWork _uow;
        private readonly ICurrentUserService _currentUserService;

        public WatchListService(
            IEmployeeWatchListRepository watchListRepository,
            IEmployeeRepository employeeRepository,
            IUnitOfWork uow,
            ICurrentUserService currentUserService)
        {
            _watchListRepository = watchListRepository;
            _employeeRepository = employeeRepository;
            _uow = uow;
            _currentUserService = currentUserService;
        }

        public async Task<Result<PaginatedResult<WatchListDto>>> GetWatchList(WatchListParam param)
        {
            var query = _watchListRepository.GetQueryable()
                .Include(x => x.Employee)
                .AsQueryable();

            if (!string.IsNullOrEmpty(param.Search))
            {
                query = query.Where(x => x.Employee.Name.Contains(param.Search));
            }

            if (!string.IsNullOrEmpty(param.NationalId))
            {
                query = query.Where(x => x.Employee.Id.Contains(param.NationalId));
            }

            if (param.TabCode.HasValue)
            {
                query = query.Where(x => x.Employee.TabCode == param.TabCode.Value);
            }

            if (param.TegaraCode.HasValue)
            {
                query = query.Where(x => x.Employee.TegaraCode == param.TegaraCode.Value);
            }

            var count = await query.CountAsync();
            var items = await query
                .OrderByDescending(x => x.CreatedAt)
                .Skip((param.PageIndex - 1) * param.PageSize)
                .Take(param.PageSize)
                .Select(x => new WatchListDto
                {
                    Id = x.Id,
                    EmployeeId = x.EmployeeId,
                    EmployeeName = x.Employee.Name,
                    Reason = x.Reason,
                    ExpiresAt = x.ExpiresAt,
                    CreatedBy = x.CreatedBy,
                    CreatedAt = x.CreatedAt,
                    IsActive = x.IsActive && (!x.ExpiresAt.HasValue || x.ExpiresAt > DateTime.Now),
                    TabCode = x.Employee.TabCode,
                    TegaraCode = x.Employee.TegaraCode
                })
                .ToListAsync();

            return Result.Success(PaginatedResult<WatchListDto>.Create(items, param.PageIndex, param.PageSize, count));
        }

        public async Task<Result<WatchListDto>> AddToWatchList(AddToWatchListRequest request)
        {
            Employee employee = null;

            switch (request.SearchType)
            {
                case "nationalId":
                    if (string.IsNullOrEmpty(request.NationalId))
                        return Result.Failure<WatchListDto>(new Error("400", "يجب إدخال الرقم القومي"));
                    employee = await _employeeRepository.GetById(request.NationalId);
                    break;

                case "tabCode":
                    if (!request.TabCode.HasValue)
                        return Result.Failure<WatchListDto>(new Error("400", "يجب إدخال كود طب"));
                    employee = await _employeeRepository.GetQueryable()
                        .FirstOrDefaultAsync(x => x.TabCode == request.TabCode.Value);
                    break;

                case "tegaraCode":
                    if (!request.TegaraCode.HasValue)
                        return Result.Failure<WatchListDto>(new Error("400", "يجب إدخال كود تجارة"));
                    employee = await _employeeRepository.GetQueryable()
                        .FirstOrDefaultAsync(x => x.TegaraCode == request.TegaraCode.Value);
                    break;

                default:
                    return Result.Failure<WatchListDto>(new Error("400", "يجب اختيار نوع البحث"));
            }

            if (employee == null)
            {
                return Result.Failure<WatchListDto>(new Error("404", "الموظف غير موجود"));
            }

            var existing = await _watchListRepository.GetQueryable()
                .FirstOrDefaultAsync(x => x.EmployeeId == employee.Id && x.IsActive);

            if (existing != null)
            {
                return Result.Failure<WatchListDto>(new Error("400", "الموظف موجود بالفعل في قائمة الانتباه"));
            }

            var newEntry = new EmployeeWatchList
            {
                EmployeeId = employee.Id,
                Reason = request.Reason,
                ExpiresAt = request.ExpiresAt,
                IsActive = true,
                CreatedAt = DateTime.Now,
                CreatedBy = _currentUserService.UserId
            };

            await _watchListRepository.Insert(newEntry);
            var result = await _uow.SaveChangesAsync() > 0;

            if (!result) return Result.Failure<WatchListDto>(new Error("500", "حدث خطأ أثناء الإضافة"));

            return Result.Success(new WatchListDto
            {
                Id = newEntry.Id,
                EmployeeId = newEntry.EmployeeId,
                EmployeeName = employee.Name,
                Reason = newEntry.Reason,
                ExpiresAt = newEntry.ExpiresAt,
                CreatedBy = newEntry.CreatedBy,
                CreatedAt = newEntry.CreatedAt,
                IsActive = newEntry.IsActive,
                TabCode = employee.TabCode,
                TegaraCode = employee.TegaraCode
            });
        }

        public async Task<Result> RemoveFromWatchList(int id)
        {
            var entry = await _watchListRepository.GetById(id);
            if (entry == null) return Result.Failure(new Error("404", "القيد غير موجود"));

            await _watchListRepository.Delete(id);
            var result = await _uow.SaveChangesAsync() > 0;

            if (!result) return Result.Failure(new Error("500", "حدث خطأ أثناء الحذف"));

            return Result.Success("تم الحذف بنجاح");
        }

        public async Task<Dictionary<string, WatchListAlertDto>> GetActiveAlerts(IEnumerable<string> employeeIds)
        {
            if (employeeIds == null || !employeeIds.Any())
                return new Dictionary<string, WatchListAlertDto>();

            var idsList = employeeIds.Distinct().ToList();
            var now = DateTime.Now;

            var alerts = await _watchListRepository.GetQueryable()
                .Where(x => idsList.Contains(x.EmployeeId) && x.IsActive)
                .Where(x => !x.ExpiresAt.HasValue || x.ExpiresAt > now)
                .Select(x => new 
                { 
                    x.EmployeeId, 
                    Alert = new WatchListAlertDto { Reason = x.Reason } 
                })
                .ToDictionaryAsync(x => x.EmployeeId, x => x.Alert);

            return alerts;
        }
    }
}
