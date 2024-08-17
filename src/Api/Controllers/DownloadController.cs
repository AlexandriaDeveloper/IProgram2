using System.Data;
using Application.Services;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers
{
    public class DownloadController : BaseApiController
    {
        private readonly IDepartmentRepository _departmentRepository;
        public DownloadController(IDepartmentRepository departmentRepository)
        {
            this._departmentRepository = departmentRepository;
        }


        [HttpGet("downloadFile")]
        public async Task<FileResult> DownlodFile([FromQuery] string fileName)
        {
            // var ms = new MemoryStream();

            string path = Path.Combine(Directory.GetCurrentDirectory(), "Content\\");
            string tempPath = Path.Combine(Path.GetTempPath() + Guid.NewGuid().ToString());
            switch (fileName)
            {
                case "employees-department":
                    path = path + "EmployeesDepartment.xlsx";
                    break;
                case "form-file":
                    path = path + "FormFile.xlsx";
                    break;
                case "add-employees":
                    path = path + "AddEmployees.xlsx";
                    tempPath = tempPath + "-AddEmployees.xlsx";
                    await WriteDepartments(path, tempPath);
                    path = tempPath;
                    break;
                default: throw new Exception("File not found");
            }
            // string filePath = Path.Combine(Directory.GetCurrentDirectory(), "Content\\" + fileName);
            if (!System.IO.File.Exists(path))
                throw new Exception("File not found");


            var memory = new MemoryStream();
            await using (var stream = new FileStream(path, FileMode.Open))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;
            return File(memory, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }


        private async Task WriteDepartments(string filePath, string tempPath)
        {
            DataTable dt;
            dt = new DataTable();
            dt.Columns.Add("Id");
            dt.Columns.Add("Name");
            var departments = await _departmentRepository.GetQueryable().Where(x => x.IsActive).ToListAsync();
            departments.ForEach(x =>
              {
                  DataRow row = dt.NewRow();
                  row["Id"] = x.Id;
                  row["Name"] = x.Name;
                  dt.Rows.Add(row);
              });

            System.IO.File.Copy(filePath, tempPath);
            var memory = new MemoryStream();
            await using (var stream = new FileStream(tempPath, FileMode.Open))
            {
                await stream.CopyToAsync(memory);
            }
            NpoiServiceProvider npoiProvider = new NpoiServiceProvider(tempPath);
            var workbook = await npoiProvider.OpenAndWriteSheetByName("Sheet2", dt, 1);
            FileStream fs;
            using (var ms = new MemoryStream())
            {
                fs = new FileStream(tempPath, System.IO.FileMode.Create);
                workbook.Write(ms);

            }
            memory.Position = 0;
            workbook.Write(fs);
        }
    }
}