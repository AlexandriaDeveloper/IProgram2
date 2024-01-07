using Application.Dtos;
using Application.Helpers;
using Application.Shared;

using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Persistence.Helpers;
using Persistence.Specifications;

namespace Application.Features
{
    public class DailyService
    {
        private readonly IDailyRepository _dailyRepository;
        private readonly IUniteOfWork _unitOfWork;
        private readonly IFormRepository _formRepository;
        private readonly ReportService _reportService;

        public DailyService(IDailyRepository dailyRepository, IFormRepository formRepository, ReportService reportService, IUniteOfWork unitOfWork)
        {
            this._formRepository = formRepository;
            this._reportService = reportService;
            this._unitOfWork = unitOfWork;
            this._dailyRepository = dailyRepository;
        }

        public async Task<Result<DailyDto>> AddDaily(DailyDto dailyDto, CancellationToken cancellationToken)
        {
            await _dailyRepository.Insert(
                   new Daily
                   {
                       Name = dailyDto.Name,
                       DailyDate = dailyDto.DailyDate,

                   }
               );

            var result = await _unitOfWork.SaveChangesAsync(cancellationToken) > 0;
            if (!result)
            {
                return Result.Failure<DailyDto>(new Error("500", "Internal Server Error"));
            }
            return Result.Success<DailyDto>(dailyDto);

        }

        public async Task<Result<DailyDto>> EditDaily(DailyDto dailyDto, CancellationToken cancellationToken)
        {

            var daily = await _dailyRepository.GetById(dailyDto.Id);
            daily.Name = dailyDto.Name;
            daily.DailyDate = dailyDto.DailyDate;
            _dailyRepository.Update(daily);

            var result = await _unitOfWork.SaveChangesAsync(cancellationToken) > 0;
            if (!result)
            {
                return Result.Failure<DailyDto>(new Error("500", "Internal Server Error"));
            }
            return Result.Success<DailyDto>(dailyDto);

        }

        public async Task<Result> SoftDeleteDaily(int id, CancellationToken cancellationToken)
        {
            var daily = await _dailyRepository.GetById(id);
            if (daily == null)
                return Result.Failure(new Error("404", "Not Found"));
            //daily.IsActive = false;
            await _dailyRepository.DeActive(daily.Id);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success("Deleted Successfully");
        }

        public async Task<Result<PaginatedResult<DailyDto>>> GetDailies(DailyParam param, CancellationToken cancellationToken)
        {
            var result = await _dailyRepository.ListAllAsync(new DailySpecification(param));
            var count = await _dailyRepository.CountAsync(new DailyCountSpecification(param));

            var resultToReturn = result.Select(x => new DailyDto
            {
                Name = x.Name,
                Id = x.Id,
                DailyDate = x.DailyDate
            }).ToList();

            var pagedResult = PaginatedResult<DailyDto>.Create(resultToReturn, param.PageIndex, param.PageSize, count);
            return Result.Success(pagedResult);
        }

        public async Task<byte[]> ExportPdf(int dailyId)
        {
            var FormsIsideDaily = _formRepository
            .GetQueryable()
            .Include(t => t.FormDetails)
            .ThenInclude(e => e.Employee)
            .Where(x => x.DailyId == dailyId)
            .ToList();


            //zip form details
            var result = FormsIsideDaily.SelectMany(x => x.FormDetails)
            .GroupBy(k => k.EmployeeId)
            .Select(g => new FormDetailsDto
            {

                EmployeeId = g.Key,
                Name = g.FirstOrDefault().Employee.Name,
                TabCode = g.FirstOrDefault().Employee.TabCode,
                TegaraCode = g.FirstOrDefault().Employee.TegaraCode,
                NationalId = g.FirstOrDefault().Employee.NationalId,
                Amount = g.Sum(x => x.Amount)
            });
            var daily = await _dailyRepository.GetById(dailyId);

            FormDto form = new FormDto
            {
                //Description = "" + " " + daily.Name,
                FormDetails = result.ToList(),
                Name = "تقرير اليومية",
                Count = result.Count(),
                TotalAmount = result.Sum(x => x.Amount),

            };

            // var result2 = result.GroupBy(k => k).ToList();


            return await _reportService.PrintPdf(form);
        }


    }
}