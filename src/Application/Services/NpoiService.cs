using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using NPOI.HSSF.UserModel;
using NPOI.HSSF.Util;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Application.Services
{
    public class NpoiServiceProvider
    {
        IWorkbook workbook;
        int headerIndex = 0;
        public NpoiServiceProvider()
        {
            if (workbook == null)
                workbook = new XSSFWorkbook();
        }
        public NpoiServiceProvider(string path)
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
        public List<string> GetHeadersBySheerName(string sheetName, int rowIndex = 0)
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
        public List<string> GetHeadersByIndex(int sheetIndex, int rowIndex = 0)
        {
            headerIndex = rowIndex;
            List<string> headers = new List<string>();
            ISheet sheet = workbook.GetSheetAt(sheetIndex);
            IRow headerRow = sheet.GetRow(rowIndex);
            for (int i = 0; i < headerRow.LastCellNum; i++)
            {
                ICell cell = headerRow.GetCell(i);
                if (cell == null || string.IsNullOrWhiteSpace(cell.ToString())) continue;
                headers.Add(cell.ToString());
            }
            return headers;
        }

        public DataTable ReadSheeBySheetName(string sheetName, int startRowIndex = 0)
        {
            ISheet sheet = workbook.GetSheet(sheetName);
            return ReadSheetData(sheet, startRowIndex);
        }
        public DataTable ReadSheeByIndex(int sheetIndex, int startRowIndex = 0)
        {
            ISheet sheet = workbook.GetSheetAt(sheetIndex);
            return ReadSheetData(sheet, startRowIndex);
        }


        //Read SheetData And ConvertItTo DataTable
        private DataTable ReadSheetData(ISheet sheetElement, int startRowIndex = 0)
        {
            headerIndex = startRowIndex;
            ISheet sheet = sheetElement;
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

        public IWorkbook CreateExcelFile(string sheetName, string Title, string[] ColumnsNames, DataTable data)
        {
            headerIndex = 1;

            ISheet sheet = workbook.CreateSheet(sheetName);
            sheet.SetMargin(MarginType.LeftMargin, 0.2);
            sheet.SetMargin(MarginType.RightMargin, 0.2);
            sheet.IsRightToLeft = true;
            IRow titleRow = sheet.CreateRow(0);
            titleRow.Height = 900;
            titleRow.CreateCell(0).SetCellValue(Title);
            titleRow.Cells[0].CellStyle = TitleStyle(workbook as XSSFWorkbook);
            sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, ColumnsNames.Length - 1));

            IRow headerRow = sheet.CreateRow(headerIndex);
            headerRow.Height = 600;

            XSSFCellStyle headerStyle = HeaderStyle(workbook as XSSFWorkbook);


            for (int i = 0; i < ColumnsNames.Length; i++)
            {
                headerRow.CreateCell(i).SetCellValue(ColumnsNames[i]);
            }


            for (int i = 0; i < 7; i++)
            {
                headerRow.Cells[i].CellStyle.SetFont(HeaderFont(workbook as XSSFWorkbook));
                headerRow.Cells[i].CellStyle = headerStyle;
            };


            for (int i = 0; i < data.Rows.Count; i++)
            {
                IRow row = sheet.CreateRow(i + headerIndex + 1);
                row.Height = 600;



                for (int j = 0; j < data.Columns.Count; j++)
                {
                    var cellType = data.Rows[i][j].GetType();
                    //check type of data
                    if (data.Rows[i][j].GetType() == typeof(double))
                    {
                        row.CreateCell(j).SetCellValue(double.Parse(data.Rows[i][j].ToString()));
                        row.Cells[j].SetCellType(CellType.Numeric);
                    }
                    else if (data.Rows[i][j].GetType() == typeof(int))
                    {
                        row.CreateCell(j).SetCellValue(int.Parse(data.Rows[i][j].ToString()));
                        row.Cells[j].SetCellType(CellType.Numeric);
                    }

                    else
                        row.CreateCell(j).SetCellValue(data.Rows[i][j].ToString());

                    sheet.AutoSizeColumn(j);
                    row.Cells[j].CellStyle = rowStyle(workbook as XSSFWorkbook);
                    row.Cells[j].CellStyle.WrapText = true;
                }
            }

            return workbook;
        }
        private IFont HeaderFont(XSSFWorkbook workbook)
        {
            var font = workbook.CreateFont();
            font.FontHeightInPoints = 12;
            font.FontName = "cairo";
            font.Color = NPOI.HSSF.Util.HSSFColor.Black.Index;
            font.Underline = FontUnderlineType.Single;
            font.IsBold = true;

            return font;
        }
        private IFont TitleFont(XSSFWorkbook workbook)
        {
            var font = workbook.CreateFont() as XSSFFont;
            font.FontHeightInPoints = 18;
            font.FontName = "Cairo";
            font.IsBold = true;
            font.Underline = FontUnderlineType.Single;
            font.Color = NPOI.HSSF.Util.HSSFColor.Black.Index;
            font.Underline = FontUnderlineType.Single;

            return font;
        }
        private IFont RowFont(XSSFWorkbook workbook)
        {
            var font = workbook.CreateFont() as XSSFFont;
            font.FontHeightInPoints = 12;
            font.FontName = "Arial";
            font.Color = NPOI.HSSF.Util.HSSFColor.Black.Index;
            return font;
        }

        private XSSFCellStyle HeaderStyle(XSSFWorkbook Workbook)
        {
            var headerCellStyle = (XSSFCellStyle)workbook.CreateCellStyle();

            // headerCellStyle.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.Grey80Percent.Index;
            headerCellStyle.SetFillForegroundColor(new XSSFColor(new byte[] { 200, 200, 200 }));
            headerCellStyle.FillPattern = FillPattern.SolidForeground;
            headerCellStyle.Alignment = HorizontalAlignment.Center;
            headerCellStyle.VerticalAlignment = VerticalAlignment.Center;
            headerCellStyle.BorderBottom = BorderStyle.Thin;
            headerCellStyle.BorderLeft = BorderStyle.Thin;
            headerCellStyle.BorderRight = BorderStyle.Thin;
            headerCellStyle.BorderTop = BorderStyle.Thin;
            headerCellStyle.WrapText = true;
            headerCellStyle.BorderDiagonalLineStyle = BorderStyle.Thin;
            headerCellStyle.SetFont(HeaderFont(Workbook as XSSFWorkbook));

            return headerCellStyle;
        }
        private XSSFCellStyle rowStyle(XSSFWorkbook Workbook)
        {
            var headerCellStyle = (XSSFCellStyle)workbook.CreateCellStyle();


            headerCellStyle.Alignment = HorizontalAlignment.Center;
            headerCellStyle.VerticalAlignment = VerticalAlignment.Center;
            headerCellStyle.BorderBottom = BorderStyle.Thin;
            headerCellStyle.BorderLeft = BorderStyle.Thin;
            headerCellStyle.BorderRight = BorderStyle.Thin;
            headerCellStyle.BorderTop = BorderStyle.Thin;
            headerCellStyle.WrapText = true;
            headerCellStyle.BorderDiagonalLineStyle = BorderStyle.Thin;
            headerCellStyle.SetFont(RowFont(Workbook as XSSFWorkbook));

            return headerCellStyle;
        }
        private XSSFCellStyle TitleStyle(XSSFWorkbook Workbook)
        {
            var headerCellStyle = (XSSFCellStyle)workbook.CreateCellStyle();
            headerCellStyle.Alignment = HorizontalAlignment.Center;
            headerCellStyle.VerticalAlignment = VerticalAlignment.Center;
            headerCellStyle.WrapText = true;
            headerCellStyle.SetFont(TitleFont(Workbook as XSSFWorkbook));
            return headerCellStyle;
        }

    }


}

