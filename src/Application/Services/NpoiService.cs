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
    public class NpoiServiceProvider
    {
        private IWorkbook workbook;
        private int headerIndex = 0;
        private XSSFCellStyle headerStyle; // نمط العناوين
        private XSSFCellStyle rowStyle;    // نمط الصفوف
        private XSSFCellStyle titleStyle;  // نمط العنوان

        public NpoiServiceProvider()
        {
            if (workbook == null)
            {
                workbook = new XSSFWorkbook();
                InitializeStyles(); // تهيئة الأنماط مرة واحدة
            }
        }

        public NpoiServiceProvider(string path)
        {
            string extension = Path.GetExtension(path);
            if (extension == ".xls")
            {
                workbook = new HSSFWorkbook();
            }
            else if (extension == ".xlsx")
            {
                workbook = WorkbookFactory.Create(path);
            }
            else
            {
                throw new Exception("Invalid file type");
            }
            InitializeStyles(); // تهيئة الأنماط بعد تحميل الملف
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
        // تهيئة الأنماط مرة واحدة
        private void InitializeStyles()
        {
            var xssfWorkbook = workbook as XSSFWorkbook;
            headerStyle = HeaderStyle(xssfWorkbook);
            rowStyle = RowStyle(xssfWorkbook);
            titleStyle = TitleStyle(xssfWorkbook);
        }

        // ... (دوال أخرى مثل GetSheetsName و GetHeadersBySheerName بدون تغيير)

        public async Task<IWorkbook> CreateExcelFile(string sheetName, string[] ColumnsNames, DataTable data, string Title = null)
        {
            headerIndex = Title != null ? 1 : 0;

            ISheet sheet = workbook.CreateSheet(sheetName.Length > 31 ? sheetName.Substring(0, 31) : sheetName);
            sheet.SetMargin(MarginType.LeftMargin, 0.2);
            sheet.SetMargin(MarginType.RightMargin, 0.2);
            sheet.IsRightToLeft = true;

            if (Title != null)
            {
                IRow titleRow = sheet.CreateRow(0);
                titleRow.Height = 900;
                titleRow.CreateCell(0).SetCellValue(Title);
                titleRow.Cells[0].CellStyle = titleStyle; // استخدام نمط العنوان المُعد مسبقًا
                sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, ColumnsNames.Length - 1));
            }

            IRow headerRow = sheet.CreateRow(headerIndex);
            headerRow.Height = 600;

            for (int i = 0; i < ColumnsNames.Length; i++)
            {
                headerRow.CreateCell(i).SetCellValue(ColumnsNames[i]);
                headerRow.Cells[i].CellStyle = headerStyle; // استخدام نمط العناوين المُعد مسبقًا
            }

            for (int i = 0; i < data.Rows.Count; i++)
            {
                IRow row = sheet.CreateRow(i + headerIndex + 1);
                row.Height = 600;
                await CreateRow(i + headerIndex + 1, data.Rows[i], row);
            }

            for (int i = 0; i < ColumnsNames.Length; i++)
            {
                sheet.AutoSizeColumn(i);
            }
            return workbook;
        }

        public async Task<IWorkbook> OpenAndWriteSheetByName(string sheetName, DataTable data, int rowIndex = 0)
        {
            ISheet sheet = workbook.GetSheet(sheetName);
            sheet.IsRightToLeft = true;

            foreach (DataRow row in data.Rows)
            {
                await CreateRow(rowIndex, row, sheet.CreateRow(rowIndex++));
            }
            return workbook;
        }
        private ICell CheckCell(ICell cell, object value)
        {
            //check if value is double or not
            if (value is double)
            {
                //how to cast object to double

                cell.SetCellType(CellType.Numeric);
                cell.SetCellValue(double.TryParse(value.ToString(), out double result) ? result : 0);
                return cell;

            }
            else if (value is string)
            {
                cell.SetCellType(CellType.String);
                cell.SetCellValue(value.ToString());
                return cell;
            }
            return cell;


        }

        private async Task CreateRow(int rowIndex, DataRow row, IRow rowElement)
        {
            for (int i = 0; i < row.ItemArray.Length; i++)
            {
                rowElement.CreateCell(i);

                if (row.ItemArray.Length > i)
                {
                    // rowElement.GetCell(0).SetCellValue(double.TryParse(row.ItemArray[0].ToString(), out double result) ? result : 0);
                    CheckCell(rowElement.GetCell(i), row.ItemArray[i].ToString());
                    rowElement.Cells[i].CellStyle = rowStyle; // استخدام نمط الصفوف المُعد مسبقًا
                }
            }

            // if (row.ItemArray.Length >= 1)
            // {
            //     // rowElement.GetCell(0).SetCellValue(double.TryParse(row.ItemArray[0].ToString(), out double result) ? result : 0);
            //     CheckCell(rowElement.GetCell(0), row.ItemArray[0].ToString());
            //     rowElement.Cells[0].CellStyle = rowStyle; // استخدام نمط الصفوف المُعد مسبقًا
            // }
            // if (row.ItemArray.Length >= 2)
            // {
            //     CheckCell(rowElement.GetCell(1), row.ItemArray[1].ToString());
            //     // rowElement.GetCell(1).SetCellValue(row[1].ToString());
            //     rowElement.Cells[1].CellStyle = rowStyle;
            // }
            // if (row.ItemArray.Length >= 3)
            // {
            //     //  rowElement.GetCell(2).SetCellValue(double.TryParse(row.ItemArray[2].ToString(), out double result4) ? result4 : 0);
            //     CheckCell(rowElement.GetCell(2), row.ItemArray[2].ToString());
            //     rowElement.Cells[2].CellStyle = rowStyle;
            // }
            // if (row.ItemArray.Length >= 4)
            // {
            //     //rowElement.GetCell(3).SetCellValue(double.TryParse(row.ItemArray[3].ToString(), out double result2) ? result2 : 0);
            //     CheckCell(rowElement.GetCell(3), row.ItemArray[3].ToString());
            //     rowElement.Cells[3].CellStyle = rowStyle;
            // }
            // if (row.ItemArray.Length >= 5)
            // {
            //     // rowElement.GetCell(4).SetCellValue(row.ItemArray[4].ToString());
            //     CheckCell(rowElement.GetCell(4), row.ItemArray[4].ToString());
            //     rowElement.Cells[4].CellStyle = rowStyle;
            // }
            // if (row.ItemArray.Length >= 6)
            // {
            //     // rowElement.GetCell(5).SetCellValue(row.ItemArray[5].ToString());
            //     CheckCell(rowElement.GetCell(5), row.ItemArray[5].ToString());
            //     rowElement.Cells[5].CellStyle = rowStyle;
            // }
            // if (row.ItemArray.Length >= 7)
            // {
            //     // rowElement.GetCell(6).SetCellValue(double.TryParse(row.ItemArray[6].ToString(), out double result3) ? result3 : 0);
            //     CheckCell(rowElement.GetCell(6), row.ItemArray[6].ToString());
            //     rowElement.Cells[6].CellStyle = rowStyle;
            // }
            // if (row.ItemArray.Length >= 8)
            // {
            //     // rowElement.GetCell(6).SetCellValue(double.TryParse(row.ItemArray[6].ToString(), out double result3) ? result3 : 0);
            //     CheckCell(rowElement.GetCell(7), row.ItemArray[7].ToString());
            //     rowElement.Cells[7].CellStyle = rowStyle;
            // }
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

        private XSSFCellStyle HeaderStyle(XSSFWorkbook workbook)
        {
            var headerCellStyle = (XSSFCellStyle)workbook.CreateCellStyle();
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
            headerCellStyle.SetFont(HeaderFont(workbook));
            return headerCellStyle;
        }

        private XSSFCellStyle RowStyle(XSSFWorkbook workbook)
        {
            var cellStyle = (XSSFCellStyle)workbook.CreateCellStyle();
            cellStyle.Alignment = HorizontalAlignment.Center;
            cellStyle.VerticalAlignment = VerticalAlignment.Center;
            cellStyle.BorderBottom = BorderStyle.Thin;
            cellStyle.BorderLeft = BorderStyle.Thin;
            cellStyle.BorderRight = BorderStyle.Thin;
            cellStyle.BorderTop = BorderStyle.Thin;
            cellStyle.WrapText = true;
            cellStyle.BorderDiagonalLineStyle = BorderStyle.Thin;
            cellStyle.SetFont(RowFont(workbook));
            return cellStyle;
        }

        private XSSFCellStyle TitleStyle(XSSFWorkbook workbook)
        {
            var headerCellStyle = (XSSFCellStyle)workbook.CreateCellStyle();
            headerCellStyle.Alignment = HorizontalAlignment.Center;
            headerCellStyle.VerticalAlignment = VerticalAlignment.Center;
            headerCellStyle.WrapText = true;
            headerCellStyle.SetFont(TitleFont(workbook));
            return headerCellStyle;
        }
    }
}