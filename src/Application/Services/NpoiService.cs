using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Application.Services
{
    public class NpoiService
    {
        IWorkbook workbook;
        int headerIndex = 0;

        public NpoiService(string path)
        {
            //get extension
            string extension = Path.GetExtension(path);
            if (extension == ".xls")
            {
                workbook = new HSSFWorkbook();
            }
            else if (extension == ".xlsx")
            {
                workbook = WorkbookFactory.Create(path); // new XSSFWorkbook();
            }
            else
            {
                throw new Exception("Invalid file type");
            }

        }

        public List<string> GetSheetsName()
        {
            List<string> workbookSheets = new List<string>();
            var sheets = workbook.GetAllNames();
            foreach (var sheet in sheets)
            {
                workbookSheets.Add(sheet.SheetName);
            }
            return workbookSheets;
        }

        //read sheet headers
        public List<string> GetHeaders(string sheetName, int rowIndex = 0)
        {
            headerIndex = rowIndex;
            List<string> headers = new List<string>();
            ISheet sheet = workbook.GetSheet(sheetName);
            IRow headerRow = sheet.GetRow(rowIndex);
            for (int i = 0; i < headerRow.LastCellNum; i++)
            {
                ICell cell = headerRow.GetCell(i);
                if (cell == null || string.IsNullOrWhiteSpace(cell.ToString())) continue;
                headers.Add(cell.ToString());
            }
            return headers;
        }

        //Read SheetData And ConvertItTo DataTable
        public DataTable ReadSheetData(string sheetName, int startRowIndex = 0)
        {
            headerIndex = startRowIndex;
            ISheet sheet = workbook.GetSheet(sheetName);
            DataTable dt = new DataTable();
            IRow headerRow = sheet.GetRow(headerIndex);
            for (int i = 0; i < headerRow.LastCellNum; i++)
            {
                ICell cell = headerRow.GetCell(i);
                if (cell == null || string.IsNullOrWhiteSpace(cell.ToString())) continue;
                dt.Columns.Add(cell.ToString());
            }

            for (int i = (headerIndex + 1); i <= sheet.LastRowNum; i++)
            {
                IRow row = sheet.GetRow(i);
                DataRow dataRow = dt.NewRow();
                for (int j = 0; j < row.LastCellNum; j++)
                {
                    ICell cell = row.GetCell(j);
                    if (cell == null || string.IsNullOrWhiteSpace(cell.ToString())) continue;
                    dataRow[j] = cell.ToString();
                }
                dt.Rows.Add(dataRow);
            }
            return dt;
        }



    }


}

