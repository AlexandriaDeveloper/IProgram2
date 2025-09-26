

using Application.Dtos;
using Application.Dtos.Requests;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.Extensions.Configuration;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Previewer;

namespace Application.Features
{
    public class ReportService
    {
        private readonly IFormRepository formRepository;
        private readonly IFormDetailsRepository formDetailsRepository;
        private readonly IUniteOfWork unitOfWork;
        private readonly IEmployeeRepository _employeeRepository;
        private readonly IConfiguration _config;

        public ReportService(IFormRepository formRepository, IFormDetailsRepository formDetailsRepository, IEmployeeRepository employeeRepository, IUniteOfWork unitOfWork, IConfiguration config)
        {
            this._config = config;
            this._employeeRepository = employeeRepository;
            this.formRepository = formRepository;
            this.formDetailsRepository = formDetailsRepository;
            this.unitOfWork = unitOfWork;
        }


        public async Task<byte[]> PrintFormWithDetailsPdf(int formId)
        {

            var request = await formRepository.GetFormWithDetailsByIdAsync(formId);

            foreach (var form in request.FormDetails)
            {
                form.Employee = await _employeeRepository.GetById(form.EmployeeId);
            }

            FormDto formToReturn = new FormDto()
            {
                Description = request.Description,
                Id = request.Id,
                Name = request.Name,
                FormDetails = request.FormDetails.Select(x => new FormDetailsDto()
                {
                    Id = x.Id,
                    FormId = x.FormId,
                    Name = x.Employee.Name,
                    TabCode = x.Employee.TabCode,
                    TegaraCode = x.Employee.TegaraCode,
                    Amount = x.Amount,
                    EmployeeId = x.EmployeeId
                }).ToList(),
                Count = request.FormDetails.Count(),
                DailyId = request.DailyId,
                TotalAmount = Math.Round(request.FormDetails.Sum(x => x.Amount), 2)
            };
            return await PrintPdf(formToReturn);
        }
        public async Task<byte[]> PrintPdf(FormDto formModel)
        {
            var totalText = NumericToLiteral.Convert(formModel.TotalAmount, false, "جنيه", "جنيهات");
            totalText = totalText.Replace("(", "");
            totalText = totalText.Replace(")", "");
            totalText = totalText.Replace("،", "");
            QuestPDF.Drawing.FontManager.RegisterFont(File.OpenRead(_config["ApiImageContent"] + "Fonts/Cairo-Regular.ttf"));

            var pdf = QuestPDF.Fluent.Document.Create(c =>
             {
                 c.Page(p =>
                  {
                      p.Foreground().PaddingTop(10).PaddingBottom(10).PaddingRight(10).PaddingLeft(10).Border(3).BorderColor("#444444");
                      p.ContentFromRightToLeft();
                      p.DefaultTextStyle(TextStyle.Default.FontFamily("Arial"));
                      p.Size(PageSizes.A4);
                      p.Header().DefaultTextStyle(TextStyle.Default.FontFamily("Cairo-Regular"));
                      p.Header().PaddingTop(0, Unit.Centimetre);
                      p.Header().ScaleHorizontal(1.5f).ScaleVertical(.8f).AlignCenter().Column(c =>
                      {
                          c.Item().Row(r =>
                          {
                              r.AutoItem().AlignCenter().Width(8, Unit.Centimetre).Height(4, Unit.Centimetre).Image(_config["ApiImageContent"] + "images.png");
                          });
                          if (!string.IsNullOrEmpty(formModel.Description))
                              c.Item().Column(c =>
                              {
                                  c.Item().ShowOnce().LineHorizontal(1).LineColor(Colors.Black);
                              });
                      });
                      p.Margin(15, QuestPDF.Infrastructure.Unit.Millimetre);
                      p.PageColor(Colors.White);
                      p.Content()
                          .PaddingVertical(1, QuestPDF.Infrastructure.Unit.Millimetre)
                          .ContentFromRightToLeft()
                          .Column(x =>
                          {
                              if (!string.IsNullOrEmpty(formModel.Description))
                                  x.Item().Row(r =>
                                  {
                                      r.RelativeItem().AlignRight().Text(t =>
                                      {
                                          t.Span(" السيد الاستاذ الدكتور / عميد كليه الطب البشرى").FontSize(18).FontFamily("Andalus").Underline().ExtraBold();
                                          t.EmptyLine();
                                      });
                                  });
                              if (!string.IsNullOrEmpty(formModel.Description))
                                  x.Item().Row(r2 =>
                                  {
                                      r2.RelativeItem().AlignCenter().Text(t =>
                                     {
                                         t.Span("تحيه طيبه و بعد ،،،،").Bold().FontSize(14);
                                         t.EmptyLine();
                                     });
                                  });
                              if (!string.IsNullOrEmpty(formModel.Description))
                                  x.Item().Row(r3 =>
                                  {
                                      r3.RelativeItem().AlignRight().Text(t =>
                                      {
                                          t.ParagraphSpacing(1, Unit.Millimetre);
                                          t.Span(formModel.Description).FontSize(14);
                                          t.EmptyLine();
                                      });
                                  });
                              x.Item().Border(1).Table(t =>
                              {
                                  t.ColumnsDefinition(h =>
                                  {
                                      h.ConstantColumn(35);
                                      h.ConstantColumn(50);
                                      h.ConstantColumn(50);
                                      h.RelativeColumn();
                                      h.RelativeColumn();
                                      h.ConstantColumn(80);
                                  });
                                  t.Header(h =>
                                  {
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                         t =>
                                         {
                                             t.Span("م").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                            t =>
                                         {
                                             t.Span("كود تجارة").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                         {
                                             t.Span("كود طب").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                         {
                                             t.Span("الرقم القومى").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                         {
                                             t.Span("الاسم").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                         {
                                             t.Span("المبلغ").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                  });
                                  uint row = 0;
                                  foreach (var form in formModel.FormDetails)
                                  {
                                      t.Cell().Row((uint)row + 1).Column(1).Border(1).Padding(2).AlignMiddle().AlignCenter().Text((row + 1).ToString());
                                      t.Cell().Row((uint)row + 1).Column(2).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.TegaraCode.ToString());
                                      t.Cell().Row((uint)row + 1).Column(3).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.TabCode.ToString());
                                      t.Cell().Row((uint)row + 1).Column(4).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.EmployeeId.ToString());
                                      t.Cell().Row((uint)row + 1).Column(5).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Name);
                                      t.Cell().Row((uint)row + 1).Column(6).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Amount.ToString());
                                      row++;
                                  }
                                  t.Cell().Row((row + 1)).ColumnSpan(5).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(
                                     t =>
                                     {
                                         t.Span(" اجمالى المبلغ : " + totalText + "فقط لا غير").Bold().FontSize(9).FontFamily("Cairo");
                                     });
                                  t.Cell().Row((row + 1)).Column(6).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text
                                  (formModel.TotalAmount.ToString()).Bold().FontFamily("Cairo");
                              });

                              if (!string.IsNullOrEmpty(formModel.Description))
                                  x.Item().Text(t =>
                                  {
                                      t.EmptyLine();
                                      t.AlignCenter();
                                      t.Span("و تفضلوا سيادتكم بقبول وافر الشكر و الاحترام").FontSize(16).FontFamily("Andalus").Bold();
                                  });
                          });
                      p.Background()
                      .AlignBottom()
                      .Image(_config["ApiImageContent"] + "logo3.png");
                      p.Footer()
                             .Table(t =>
                             {
                                 t.ColumnsDefinition(c =>
                                 {
                                     c.RelativeColumn();
                                     c.RelativeColumn();
                                     c.RelativeColumn();
                                 });
                                 t.Cell().Row(1).Column(1).AlignRight().Text("الموظف المختص").Bold();
                                 t.Cell().Row(1).Column(2).AlignCenter().Text("رئيس القسم").Bold();
                                 t.Cell().Row(1).Column(3).AlignLeft().Text("رئيس المصلحة").Bold();
                                 t.Cell().Row(2).Column(2).AlignCenter().Text(x =>
                                 {
                                     x.EmptyLine();
                                     x.EmptyLine();
                                     x.Span("-").FontSize(12).FontColor("#484848");
                                     x.CurrentPageNumber().FontSize(12).FontColor("#484848");
                                     x.Span("-").FontSize(12).FontColor("#484848");
                                 });
                             });
                  });
             }).WithMetadata(new DocumentMetadata()
             {
                 Title = "Report",
                 Author = "mohamed",
                 Subject = "hello",
                 Keywords = "Test"
             })
        .GeneratePdf();
            //pdf.show
            return await Task.FromResult(pdf);
        }

        public async Task<byte[]> PrintIndexPdf(DailyDto formModel)
        {
            var totalText = NumericToLiteral.Convert(Math.Round(formModel.Forms.Sum(x => x.TotalAmount), 2), false, "جنيه", "جنيهات");
            totalText = totalText.Replace("(", "");
            totalText = totalText.Replace(")", "");
            totalText = totalText.Replace("،", "");
            QuestPDF.Drawing.FontManager.RegisterFont(File.OpenRead(_config["ApiImageContent"] + "Fonts/Cairo-Regular.ttf"));
            var pdf = QuestPDF.Fluent.Document.Create(c =>
             {
                 c.Page(p =>
                  {
                      p.Foreground().PaddingTop(10).PaddingBottom(10).PaddingRight(10).PaddingLeft(10).Border(3).BorderColor("#444444");
                      p.ContentFromRightToLeft();
                      p.DefaultTextStyle(TextStyle.Default.FontFamily("Arial"));
                      p.Size(PageSizes.A4);
                      p.Header().DefaultTextStyle(TextStyle.Default.FontFamily("Cairo"));
                      p.Header().PaddingTop(0, Unit.Centimetre);
                      p.Header().ScaleHorizontal(1.5f).ScaleVertical(.8f).AlignCenter().Column(c =>
                      {
                          c.Item().Row(r =>
                          {
                              r.AutoItem().AlignCenter().Width(8, Unit.Centimetre).Height(4, Unit.Centimetre).Image(_config["ApiImageContent"] + "images.png");
                          });
                          //   if (!string.IsNullOrEmpty(formModel.Description))
                          //       c.Item().Column(c =>
                          //       {
                          //           c.Item().ShowOnce().LineHorizontal(1).LineColor(Colors.Black);
                          //       });
                      });
                      p.Margin(15, QuestPDF.Infrastructure.Unit.Millimetre);
                      p.PageColor(Colors.White);
                      p.Content()
                          .PaddingVertical(1, QuestPDF.Infrastructure.Unit.Millimetre)
                          .ContentFromRightToLeft()
                          .Column(x =>
                          {
                              x.Item().Row(r2 =>
                              {
                                  r2.RelativeItem().AlignCenter().Text(t =>
                                 {
                                     t.Span(" تقرير يوميه ").Bold().FontSize(14).FontFamily("Cairo").Underline();
                                     t.Span(formModel.DailyDate.ToShortDateString()).Bold().FontSize(14).FontFamily("Cairo").Underline();
                                     //t.EmptyLine();

                                 });
                              });

                              x.Item().Border(1).Table(t =>
                              {
                                  t.ColumnsDefinition(h =>
                                  {
                                      h.ConstantColumn(35);
                                      h.ConstantColumn(50);
                                      h.RelativeColumn();
                                      h.ConstantColumn(100);

                                  });
                                  t.Header(h =>
                                  {
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                         t =>
                                         {
                                             t.Span("م").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                            t =>
                                         {
                                             t.Span("الرقم").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                         {
                                             t.Span("البيان").Bold().FontFamily("Cairo").FontSize(10);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                         {
                                             t.Span("المبلغ").Bold().FontFamily("Cairo").FontSize(10);
                                         });

                                  });
                                  uint row = 0;
                                  foreach (var form in formModel.Forms)
                                  {
                                      t.Cell().Row((uint)row + 1).Column(1).Border(1).Padding(2).AlignMiddle().AlignCenter().Text((row + 1).ToString());
                                      t.Cell().Row((uint)row + 1).Column(2).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Index.ToString());
                                      t.Cell().Row((uint)row + 1).Column(3).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Name.ToString());
                                      t.Cell().Row((uint)row + 1).Column(4).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.TotalAmount.ToString());
                                      row++;
                                  }
                                  t.Cell().Row((row + 1)).ColumnSpan(3).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(
                                     t =>
                                     {
                                         t.Span(" اجمالى المبلغ : " + totalText + "فقط لا غير").Bold().FontSize(9).FontFamily("Cairo");
                                     });
                                  t.Cell().Row((row + 1)).Column(4).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text
                                  (Math.Round(formModel.Forms.Sum(x => x.TotalAmount), 2).ToString()).Bold().FontFamily("Cairo");
                              });


                          });
                      p.Background()
                      .AlignBottom()
                      .Image(_config["ApiImageContent"] + "logo3.png");
                      p.Footer()
                             .Table(t =>
                             {
                                 t.ColumnsDefinition(c =>
                                 {
                                     c.RelativeColumn();
                                     c.RelativeColumn();
                                     c.RelativeColumn();
                                 });
                                 t.Cell().Row(1).Column(1).AlignRight().Text("الموظف المختص").Bold();
                                 t.Cell().Row(1).Column(2).AlignCenter().Text("رئيس القسم").Bold();
                                 t.Cell().Row(1).Column(3).AlignLeft().Text("رئيس المصلحة").Bold();
                                 t.Cell().Row(2).Column(2).AlignCenter().Text(x =>
                                 {
                                     x.EmptyLine();
                                     x.EmptyLine();
                                     x.Span("-").FontSize(12).FontColor("#484848");
                                     x.CurrentPageNumber().FontSize(12).FontColor("#484848");
                                     x.Span("-").FontSize(12).FontColor("#484848");
                                 });
                             });
                  });
             }).WithMetadata(new DocumentMetadata()
             {
                 Title = "Report",
                 Author = "mohamed",
                 Subject = "hello",
                 Keywords = "Test"
             })
            .GeneratePdf();
            //pdf.show
            return await Task.FromResult(pdf);
        }


        public async Task<byte[]> PrintEmployeeReportDetailsPdf(EmployeeReportDto formModel)
        {
            var nationalIdChars = formModel.NationalId.ToCharArray();
            Array.Reverse(nationalIdChars);
            var nationalId = new string(nationalIdChars);

            var tabCodeChars = formModel.TabCode.ToString().ToCharArray();
            Array.Reverse(tabCodeChars);
            var tabCode = new string(tabCodeChars);

            var tegaraCodeChars = formModel.TegaraCode.ToString().ToCharArray();
            Array.Reverse(tegaraCodeChars);
            var tegaraCode = new string(tegaraCodeChars);


            var totalText = NumericToLiteral.Convert(Math.Round(formModel.GrandTotal, 2), false, "جنيه", "جنيهات");
            totalText = totalText.Replace("(", "");
            totalText = totalText.Replace(")", "");
            totalText = totalText.Replace("،", "");
            QuestPDF.Drawing.FontManager.RegisterFont(File.OpenRead(_config["ApiImageContent"] + "Fonts/Cairo-Regular.ttf"));

            var pdf = QuestPDF.Fluent.Document.Create(c =>
             {

                 c.Page(p =>
                  {

                      p.Foreground().PaddingTop(10).PaddingBottom(10).PaddingRight(10).PaddingLeft(10).Border(3).BorderColor("#444444");
                      p.ContentFromRightToLeft();
                      p.DefaultTextStyle(TextStyle.Default.FontFamily("Arial"));
                      p.Size(PageSizes.A4);

                      p.Header().DefaultTextStyle(TextStyle.Default.FontFamily("Arial"));
                      p.Header().ContentFromRightToLeft();
                      p.Header().PaddingTop(0, Unit.Centimetre);
                      p.Header().ScaleHorizontal(1.5f).ScaleVertical(.8f).AlignCenter().Column(c =>
                      {
                          c.Item().Row(r =>
                          {
                              r.AutoItem().AlignCenter().Width(8, Unit.Centimetre).Height(4, Unit.Centimetre).Image(_config["ApiImageContent"] + "images.png");
                          });

                      });
                      p.Margin(15, QuestPDF.Infrastructure.Unit.Millimetre);
                      p.PageColor(Colors.White);
                      p.Content()
                          .PaddingVertical(1, QuestPDF.Infrastructure.Unit.Millimetre)

                          .Column(x =>
                          {

                              x.Item().AlignMiddle().Row(r =>
                              {
                                  r.RelativeItem(200)
                                  .AlignCenter()
                                  .Text($"تقرير  صرفيه")
                                  .FontSize(18)
                                  .Underline()
                                  .Bold()
                                  .FontFamily("Andalus").FontColor("#1d2281");
                              });
                              x.Item().AlignMiddle().Row(r =>
                       {
                           r.RelativeItem(200)
                           .AlignCenter()
                             .PaddingBottom(7, Unit.Millimetre)
                           .Text($"{formModel.Name}")
                           .FontSize(12)
                           .Bold()
                           .FontFamily("Cairo").FontColor("#1d2281");
                       });

                              x.Item().Row(r =>
                                {
                                    r.ConstantItem(200).Text($"الرقم القومى :  {nationalId}").DirectionAuto().FontSize(12).FontFamily("arial").SemiBold();
                                    r.ConstantItem(150).Text($"كود طب :  {tabCode}").FontSize(12).FontFamily("arial").SemiBold();
                                    r.ConstantItem(150).Text($"كود تجارة :  {tegaraCode}").FontSize(12).FontFamily("arial").SemiBold();

                                });



                              x.Item().Border(1).Table(t =>
                              {
                                  t.ColumnsDefinition(h =>
                                  {
                                      h.ConstantColumn(35);
                                      h.ConstantColumn(35);
                                      //h.RelativeColumn();
                                      h.ConstantColumn(250);
                                      h.RelativeColumn();
                                      // h.ConstantColumn(35);

                                  });
                                  t.Header(h =>
                                  {
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                         t =>
                                         {
                                             t.Span("م").ExtraBold().FontFamily("Cairo").FontSize(12);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                        t =>
                                        {
                                            t.Span("رقم").ExtraBold().FontFamily("Cairo").FontSize(12);
                                        });
                                      //   h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                      //     t =>
                                      //     {
                                      //         t.Span("اليوميه").ExtraBold().FontFamily("Cairo").FontSize(12);
                                      //     });

                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                            t =>
                                         {
                                             t.Span("البيان").ExtraBold().Bold().FontFamily("Cairo").FontSize(12);
                                         });
                                      h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                         {
                                             t.Span("المبلغ").Bold().FontFamily("Cairo").FontSize(12);
                                         });
                                      //   h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                      //      {
                                      //          t.Span("الحاله").Bold().FontFamily("Cairo").FontSize(12);
                                      //      });

                                  });
                                  uint row = 0;
                                  uint dailyCounter = 0;
                                  foreach (var daily in formModel.Dailies)
                                  {
                                      uint formCounter = 0;
                                      foreach (var form in daily.Forms)
                                      {
                                          t.Cell().Row((uint)row + 1).Column(1).Border(1).Padding(2).AlignMiddle().AlignCenter().Text((formCounter + 1).ToString());
                                          t.Cell().Row((uint)row + 1).Column(2).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.FormIndex.ToString());
                                          // t.Cell().Row((uint)row + 1).Column(3).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(daily.DailyName.ToString());
                                          t.Cell().Row((uint)row + 1).Column(3).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.FormName.ToString());
                                          t.Cell().Row((uint)row + 1).Column(4).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Amount.ToString());
                                          // t.Cell().Row((uint)row + 1).Column(4).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(string.Empty);
                                          formCounter++;
                                          row++;

                                      }
                                      t.Cell().Row((uint)row + 1).Column(1).Background("#d0d0d0").BorderBottom(1).BorderTop(1).Height(1, Unit.Centimetre).AlignMiddle().AlignCenter().Text((dailyCounter + 1).ToString()).Bold();

                                      t.Cell().Row((uint)row + 1).Column(2).ColumnSpan(2).Background("#d0d0d0").BorderBottom(1).BorderTop(1).Height(1, Unit.Centimetre).AlignMiddle().AlignCenter().Text("  إجمالى  " + daily.DailyName).Bold();
                                      //   t.Cell().Row((uint)row + 1).Column(3).Background("#d0d0d0").BorderBottom(1).BorderTop(1).Height(1, Unit.Centimetre).AlignMiddle().AlignCenter().Text(daily.TotalAmount.ToString()).Bold();
                                      t.Cell().Row((uint)row + 1).Column(4).Background("#d0d0d0").Border(1).Padding(2).AlignMiddle().AlignCenter().Text(daily.TotalAmount.ToString()).Bold();
                                      dailyCounter++;
                                      row++;
                                  }
                                  t.Cell().Row((row + 1)).ColumnSpan(3).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(
                                     t =>
                                     {
                                         t.Span("الاجمالى الكلى").Bold().FontSize(12).FontFamily("Cairo");
                                         //  t.Span(" اجمالى المبلغ : " + totalText + "فقط لا غير").Bold().FontSize(9).FontFamily("Cairo");
                                     });
                                  t.Cell().Row((row + 1)).Column(4).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text
                                  (formModel.GrandTotal.ToString()).Bold().FontFamily("Cairo");
                              });




                              x.Item().AlignMiddle().Row(r =>
                              {
                                  r.RelativeItem(200).Text("اجمالى المبلغ : " + totalText + " فقط لا غير ").FontSize(10).Bold().FontFamily("Cairo");

                              });


                          });
                      p.Background()
                      .AlignBottom()
                      .Image(_config["ApiImageContent"] + "logo3.png");
                      p.Footer()
                                           .Table(t =>
                                           {
                                               t.ColumnsDefinition(c =>
                                 {
                                     c.RelativeColumn();
                                     c.RelativeColumn();
                                     c.RelativeColumn();
                                 });
                                               t.Cell().Row(1).Column(1).AlignRight().Text("الموظف المختص").Bold();
                                               t.Cell().Row(1).Column(2).AlignCenter().Text("رئيس القسم").Bold();
                                               t.Cell().Row(1).Column(3).AlignLeft().Text("رئيس المصلحة").Bold();
                                               t.Cell().Row(2).Column(2).AlignCenter().Text(x =>
                                 {
                                     x.EmptyLine();
                                     x.EmptyLine();
                                     x.Span("-").FontSize(12).FontColor("#484848");
                                     x.CurrentPageNumber().FontSize(12).FontColor("#484848");
                                     x.Span("-").FontSize(12).FontColor("#484848");
                                 });
                                           });
                  });
             }).WithMetadata(new DocumentMetadata()
             {
                 Title = "Report",
                 Author = "mohamed",
                 Subject = "hello",
                 Keywords = "Test"
             }).GeneratePdf();
            return await Task.FromResult(pdf);
        }
    }
}