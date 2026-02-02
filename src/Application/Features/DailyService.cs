using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Dtos;
using Application.Helpers;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NPOI.SS.UserModel;
using Persistence.Helpers;
using Persistence.Repository;
using Persistence.Specifications;
using Persistence.Extensions;

namespace Application.Features
{
    public class DailyService
    {
        private readonly IDailyRepository _dailyRepository;
        private readonly IDailyReferencesRepository _dailyReferenceRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IFormRepository _formRepository;
        private readonly ReportService _reportService;
        private readonly UserManager<ApplicationUser> _userManager;
        private IConfiguration _config;

        public DailyService(IDailyRepository dailyRepository, IFormRepository formRepository, ReportService reportService, IUnitOfWork unitOfWork, UserManager<ApplicationUser> userManager, IDailyReferencesRepository dailyReferenceRepository, IConfiguration config)
        {
            this._formRepository = formRepository;
            this._reportService = reportService;
            this._unitOfWork = unitOfWork;
            this._dailyRepository = dailyRepository;
            this._config = config;
            this._userManager = userManager;
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

            int maxIndex = lastDaily.HasValue ? lastDaily.Value + 1 : 1;



            foreach (var form in daily.Forms)
            {
                if (form.IsActive)
                    form.Index = maxIndex++;

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



                }).OrderBy(x => x.Index).ToList()
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
            int i = 1;
            // foreach (var form in daily)
            // {
            //     var title = form.Name;
            //     var sheerName = i.ToString() + "-" + form.Name;
            //     i++;
            //     DataTable dt = new DataTable();
            //     dt.Columns.Add("م", typeof(int));
            //     dt.Columns.Add("الرقم القومى", typeof(string));
            //     dt.Columns.Add("كود طب", typeof(string));
            //     dt.Columns.Add("كود تجارة", typeof(string));
            //     dt.Columns.Add("القسم", typeof(string));
            //     dt.Columns.Add("الاسم", typeof(string));
            //     dt.Columns.Add("المبلغ", typeof(double));
            //     int counter = 1;

            //     foreach (var item in form.FormDetails)
            //     {
            //         DataRow dr = dt.NewRow();
            //         dr.SetField("م", counter++);
            //         dr["الرقم القومى"] = item.Employee.NationalId;
            //         dr["كود طب"] = item.Employee.TabCode;
            //         dr["كود تجارة"] = item.Employee.TegaraCode;
            //         dr["القسم"] = item.Employee.Department == null ? "" : item.Employee.Department.Name;
            //         dr["الاسم"] = item.Employee.Name;
            //         dr.SetField("المبلغ", (double)item.Amount);
            //         dt.Rows.Add(dr);
            //     }
            //   workbook = await npoi.CreateExcelFile(sheerName, new string[] { "م", "الرقم القومى", "كود طب", "كود تجارة", "القسم", "الاسم", "المبلغ" }, dt, title);

            // }


            var dailyToDataTable = daily.SelectMany(x => x.FormDetails).GroupBy(x => x.EmployeeId)
             .Select(g => new
             {
                 EmployeeId = g.Key,
                 Name = g.FirstOrDefault().Employee.Name,
                 TabCode = g.FirstOrDefault().Employee.TabCode,
                 TegaraCode = g.FirstOrDefault().Employee.TegaraCode,
                 NationalId = g.FirstOrDefault().Employee.Id,
                 Department = g.FirstOrDefault().Employee.Department,

                 Amount = g.Sum(x => x.Amount),
                 //get all reviewers by name with amount in brackets

                 ReviewedBy = g.Select(x => x.IsReviewed).Any() ? g.Select(x => x.IsReviewed ? x.Form.Index.ToString() +
                  " - " + _userManager.GetUserByIdAsync(x.IsReviewedBy).Result + " (" + x.Amount + ")" : x.Form.Index.ToString()
                  + " -  لم يتم مراجعته   " + " (" + x.Amount + ")").ToList()
                  : new List<string> { "لم يتم مراجعته   " }.ToList()


                 //ReviewedBy = g.Select(x => x.IsReviewed).Any() ? g.Select(x => x.IsReviewedBy + " (" + x.Amount + ")").ToList() : new List<string> { "لم يتم مراجعته   " }

             });



            var title2 = "اجمالى اليوميه";
            DataTable dt2 = new DataTable();
            dt2.Columns.Add("م", typeof(int));
            dt2.Columns.Add("الرقم القومى", typeof(string));
            dt2.Columns.Add("كود طب", typeof(string));
            dt2.Columns.Add("كود تجارة", typeof(string));
            dt2.Columns.Add("القسم", typeof(string));
            dt2.Columns.Add("الاسم", typeof(string));
            dt2.Columns.Add("المبلغ", typeof(decimal));
            dt2.Columns.Add("المراجع", typeof(string));
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
                dr.SetField("المبلغ", (decimal)item.Amount);
                //add all reviewers by name  separated by comma with amount 
                dr["المراجع"] = string.Join(",", item.ReviewedBy);

                dt2.Rows.Add(dr);

            }

            workbook = await npoi.CreateExcelFile(title2.Length > 31 ? title2.Substring(0, 31) : title2, new string[] { "م", "الرقم القومى", "كود طب", "كود تجارة", "القسم", "الاسم", "المبلغ", "المراجع" }, dt2, title2);



            if (workbook == null)
            {
                return null;
            }
            string tempPath = Path.GetTempPath();
            string filePath = Path.Combine(tempPath, Path.GetTempFileName() + ".xlsx");
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
        public async Task<MemoryStream> CreateJSONFile(int dailyId)
        {

            var daily = _dailyRepository.GetQueryable()
            .Include(x => x.Forms)
            .ThenInclude(d => d.FormDetails.OrderBy(x => x.OrderNum))
            .ThenInclude(e => e.Employee)
            .ThenInclude(e => e.Department)
            .Where(x => x.Id == dailyId)
            .SelectMany(x => x.Forms).Where(x => x.IsActive)
            .ToList();
            // var serializer = System.Text.Json.JsonSerializer.Serialize<List<Form>>(daily, new JsonSerializerOptions()
            // {
            //     MaxDepth = 3,
            //     ReferenceHandler = ReferenceHandler.IgnoreCycles
            // });




            //Create object of FileInfo for specified path 
            string tempPath = Path.GetTempPath();
            string fileName = Path.GetTempFileName();
            string filePath = Path.Combine(tempPath, Path.GetFileNameWithoutExtension(fileName) + ".json");
            FileInfo fi = new FileInfo(filePath);

            //Open file for Read\Write

            JsonDataDto jsonFile = new JsonDataDto();
            var dailyFromDb = await _dailyRepository.GetById(daily.FirstOrDefault().DailyId.Value);

            jsonFile.DailyDate = dailyFromDb.DailyDate;
            jsonFile.Name = dailyFromDb.Name;

            //Create StreamWriter object to write string to FileSream

            foreach (var form in daily)
            {
                jsonFile.Forms.Add(new JsonDataDto.JsonForm()
                {
                    Name = form.Name,
                    Description = form.Description,
                    Index = form.Index,
                    FormDetails = form.FormDetails.Select(x => new JsonDataDto.JsonForm.JsonFormDetails()
                    {
                        Amount = x.Amount,
                        EmployeeId = x.EmployeeId,
                        OrderNum = x.OrderNum,

                    }).ToList()
                });
            }



            // var obj = JsonConvert.SerializeObject(jsonFile);

            var memory = new MemoryStream();
            var sw = new StreamWriter(memory);

            FileStream fs = new FileStream(filePath, System.IO.FileMode.Create);
            await System.Text.Json.JsonSerializer.SerializeAsync(fs, jsonFile);
            fs.Close();
            // using (var ms = new MemoryStream())
            // {
            //     fs = new FileStream(filePath, System.IO.FileMode.Create);

            //     await fs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(obj), 0, obj.Length);
            //     fs.Close();
            // }

            await using (var stream = new FileStream(filePath, FileMode.OpenOrCreate))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;

            return memory;
            // await sw.WriteAsync(obj);
            // sw.Close();

            // await using (var stream = new FileStream(filePath, FileMode.OpenOrCreate))
            // {
            //     await stream.CopyToAsync(memory);
            // }
            // memory.Position = 0;
            // return memory;

        }

        public async Task<Result<DailyDto>> GetDaily(int dailyId, CancellationToken cancellationToken)
        {
            Daily daily = await _dailyRepository.GetQueryable(null)
            .Include(x => x.DailyReferences)
            .FirstOrDefaultAsync(x => x.Id == dailyId);



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
                    Closed = daily.Closed,
                    DailyReferences = daily.DailyReferences.Select(x => new DailyReferenceDto
                    {
                        Id = x.Id,
                        DailyId = x.DailyId,
                        Description = x.Description,
                        ReferencePath = x.ReferencePath.StartsWith("http") ? x.ReferencePath : _config["ApiImageContent"] + "DailyReferences/" + x.ReferencePath
                    }).ToList()


                });
            }

        }
    }
}
//407-5224527-0026748
