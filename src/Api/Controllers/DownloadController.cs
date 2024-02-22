using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    public class DownloadController : BaseApiController
    {
        public DownloadController()
        {

        }


        [HttpGet("downloadFile")]
        public async Task<FileResult> DownlodFile([FromQuery] string fileName)
        {

            switch (fileName)
            {
                case "employees-department":
                    fileName = "EmployeesDepartment.xlsx";
                    break;
                case "form-file":
                    fileName = "FormFile.xlsx";
                    break;
                case "add-employees":
                    fileName = "AddEmployees.xlsx";
                    break;
            }
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), "Content\\" + fileName);
            if (!System.IO.File.Exists(filePath))
                throw new Exception("File not found");


            var memory = new MemoryStream();
            await using (var stream = new FileStream(filePath, FileMode.Open))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;
            return File(memory, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
    }
}