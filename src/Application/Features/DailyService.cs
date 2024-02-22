using System.Data;
using Application.Dtos;
using Application.Helpers;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
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
        public async Task<Result> CloseDaily(int dailyId, CancellationToken cancellationToken)
        {

            var daily = await _dailyRepository.GetQueryable().Include(x => x.Forms).FirstOrDefaultAsync(x => x.Id == dailyId);
            if (daily == null)
                return Result.Failure(new Error("404", "Not Found"));
            var lastDaily = await _formRepository.GetQueryable().MaxAsync(x => x.Index);

            int maxIndex = 0;
            if (lastDaily != null)
                maxIndex = lastDaily.HasValue ? lastDaily.Value + 1 : 0;

            foreach (var form in daily.Forms)
            {
                if (form.IsActive)
                    form.Index = maxIndex;
            }

            daily.Closed = true;

            _dailyRepository.Update(daily);

            var result = await _unitOfWork.SaveChangesAsync(cancellationToken) > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            return Result.Success();

        }
        public async Task<Result> SoftDeleteDaily(int id, CancellationToken cancellationToken)
        {
            var daily = await _dailyRepository.GetById(id);
            if (daily == null)
                return Result.Failure(new Error("404", "Not Found"));
            //daily.IsActive = false;
            if (daily.Closed)
            {
                return Result.Failure(new Error("500", "اليوميه مغلقه"));
            }
            await _dailyRepository.DeActive(daily.Id);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success("Deleted Successfully");
        }
        public async Task<Result> DeleteDaily(int id, CancellationToken cancellationToken)
        {
            var daily = await _dailyRepository.GetById(id);
            if (daily == null)
                return Result.Failure(new Error("404", "Not Found"));
            if (daily.Closed)
            {
                return Result.Failure(new Error("500", "اليوميه مغلقه"));
            }
            //daily.IsActive = false;
            await _dailyRepository.Delete(daily.Id);
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
                DailyDate = x.DailyDate,
                Closed = x.Closed
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
                Amount = Math.Round(g.Sum(x => x.Amount), 2)
            });
            var daily = await _dailyRepository.GetById(dailyId);

            FormDto form = new FormDto
            {
                //Description = "" + " " + daily.Name,
                FormDetails = result.ToList(),
                Name = "تقرير اليومية",
                Count = result.Count(),
                TotalAmount = Math.Round(result.Sum(x => x.Amount), 2)

            };

            // var result2 = result.GroupBy(k => k).ToList();


            return await _reportService.PrintPdf(form);
        }
        public async Task<byte[]> ExportIndexPdf(int dailyId)
        {
            var formsInDaily = await _dailyRepository
            .GetQueryable()
            .Include(t => t.Forms.Where(x => x.IsActive))
            .ThenInclude(t => t.FormDetails)
            .FirstOrDefaultAsync(x => x.Id == dailyId)
            ;

            DailyDto daily = new DailyDto()
            {
                Name = formsInDaily.Name,
                DailyDate = formsInDaily.DailyDate,
                Id = formsInDaily.Id,

                Forms = formsInDaily.Forms.Select(x => new FormDto()
                {
                    Index = x.Index,
                    Name = x.Name,
                    TotalAmount = Math.Round(x.FormDetails.Sum(y => y.Amount), 2),



                }).ToList()
            };





            return await _reportService.PrintIndexPdf(daily);
        }

        public async Task<MemoryStream> CreateExcelFile(int dailyId)
        {

            var daily = _dailyRepository.GetQueryable()
            .Include(x => x.Forms)
            .ThenInclude(d => d.FormDetails.OrderBy(x => x.OrderNum))
            .ThenInclude(e => e.Employee)
            .ThenInclude(e => e.Department)
            .Where(x => x.Id == dailyId && x.IsActive)
            .SelectMany(x => x.Forms).Where(x => x.IsActive)
            .ToList();

            var npoi = new NpoiServiceProvider();
            IWorkbook workbook = null;
            foreach (var form in daily)
            {
                var title = form.Name;
                DataTable dt = new DataTable();
                dt.Columns.Add("م", typeof(int));
                dt.Columns.Add("الرقم القومى", typeof(string));
                dt.Columns.Add("كود طب", typeof(string));
                dt.Columns.Add("كود تجارة", typeof(string));
                dt.Columns.Add("القسم", typeof(string));
                dt.Columns.Add("الاسم", typeof(string));
                dt.Columns.Add("المبلغ", typeof(double));
                int counter = 1;

                foreach (var item in form.FormDetails)
                {
                    DataRow dr = dt.NewRow();
                    dr.SetField("م", counter++);
                    dr["الرقم القومى"] = item.Employee.NationalId;
                    dr["كود طب"] = item.Employee.TabCode;
                    dr["كود تجارة"] = item.Employee.TegaraCode;
                    dr["القسم"] = item.Employee.Department == null ? "" : item.Employee.Department.Name;
                    dr["الاسم"] = item.Employee.Name;
                    dr.SetField("المبلغ", (double)item.Amount);
                    dt.Rows.Add(dr);
                }
                workbook = npoi.CreateExcelFile(title.Length > 31 ? title.Substring(0, 31) : title, new string[] { "م", "الرقم القومى", "كود طب", "كود تجارة", "القسم", "الاسم", "المبلغ" }, dt, title);

            }


            var dailyToDataTable = daily.SelectMany(x => x.FormDetails).GroupBy(x => x.EmployeeId)
             .Select(g => new
             {
                 EmployeeId = g.Key,
                 Name = g.FirstOrDefault().Employee.Name,
                 TabCode = g.FirstOrDefault().Employee.TabCode,
                 TegaraCode = g.FirstOrDefault().Employee.TegaraCode,
                 NationalId = g.FirstOrDefault().Employee.NationalId,
                 Department = g.FirstOrDefault().Employee.Department,
                 Amount = g.Sum(x => x.Amount)
             });
            var title2 = "اجمالى اليوميه";
            DataTable dt2 = new DataTable();
            dt2.Columns.Add("م", typeof(int));
            dt2.Columns.Add("الرقم القومى", typeof(string));
            dt2.Columns.Add("كود طب", typeof(string));
            dt2.Columns.Add("كود تجارة", typeof(string));
            dt2.Columns.Add("القسم", typeof(string));
            dt2.Columns.Add("الاسم", typeof(string));
            dt2.Columns.Add("المبلغ", typeof(double));
            int counter2 = 1;
            foreach (var item in dailyToDataTable)
            {




                DataRow dr = dt2.NewRow();
                dr.SetField("م", counter2++);
                dr["الرقم القومى"] = item.NationalId;
                dr["كود طب"] = item.TabCode;
                dr["كود تجارة"] = item.TegaraCode;
                dr["القسم"] = item.Department == null ? "" : item.Department.Name;
                dr["الاسم"] = item.Name;
                dr.SetField("المبلغ", (double)item.Amount);
                dt2.Rows.Add(dr);





            }

            workbook = npoi.CreateExcelFile(title2.Length > 31 ? title2.Substring(0, 31) : title2, new string[] { "م", "الرقم القومى", "كود طب", "كود تجارة", "القسم", "الاسم", "المبلغ" }, dt2, title2);



            if (workbook == null)
            {
                return null;
            }
            string tempPath = Path.GetTempPath();
            string filePath = Path.Combine(tempPath, "Form.xlsx");
            var memory = new MemoryStream();

            FileStream fs;
            using (var ms = new MemoryStream())
            {
                fs = new FileStream(filePath, System.IO.FileMode.Create);
                workbook.Write(fs);
            }

            await using (var stream = new FileStream(filePath, FileMode.Open))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;

            return memory;
        }

        public async Task<Result<DailyDto>> GetDaily(int dailyId, CancellationToken cancellationToken)
        {
            Daily daily = await _dailyRepository.GetById(dailyId);
            if (daily == null)
            {
                return Result.Failure<DailyDto>(new Error("500", "اليوميه غير موجودة"));
            }
            else
            {
                return Result.Success(new DailyDto
                {
                    Id = daily.Id,
                    Name = daily.Name,
                    DailyDate = daily.DailyDate,
                    Closed = daily.Closed
                });
            }

        }
    }
}