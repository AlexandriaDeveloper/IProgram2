

using Application.Dtos;
using Application.Dtos.Requests;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Application.Features
{
    public class ReportService
    {
        private readonly IFormRepository formRepository;
        private readonly IFormDetailsRepository formDetailsRepository;
        private readonly IUniteOfWork unitOfWork;
        private readonly IEmployeeRepository _employeeRepository;

        public ReportService(IFormRepository formRepository, IFormDetailsRepository formDetailsRepository, IEmployeeRepository employeeRepository, IUniteOfWork unitOfWork)
        {
            this._employeeRepository = employeeRepository;
            this.formRepository = formRepository;
            this.formDetailsRepository = formDetailsRepository;
            this.unitOfWork = unitOfWork;
        }
        public async Task<byte[]> PrintFormPdf(ReportFormPdfRequest request)
        {
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
                             r.AutoItem().AlignCenter().Width(8, Unit.Centimetre).Height(4, Unit.Centimetre).Image("../Api/Content/images.png");
                         });
                         if (!string.IsNullOrEmpty(request.Description))
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
                             if (!string.IsNullOrEmpty(request.Description))
                                 x.Item().Row(r =>
                                 {
                                     r.RelativeItem().AlignRight().Text(t =>
                                     {
                                         t.Span(" السيد الاستاذ الدكتور / عميد كليه الطب البشرى").FontSize(18).FontFamily("Andalus").Underline().ExtraBold();
                                         t.EmptyLine();
                                     });
                                 });
                             if (!string.IsNullOrEmpty(request.Description))
                                 x.Item().Row(r2 =>
                                 {

                                     r2.RelativeItem().AlignCenter().Text(t =>
                                 {
                                     t.Span("تحيه طيبه و بعد ،،،،").Bold().FontSize(14);
                                     t.EmptyLine();
                                 });
                                 });
                             if (!string.IsNullOrEmpty(request.Description))
                                 x.Item().Row(r3 =>
                                 {
                                     r3.RelativeItem().AlignRight().Text(t =>
                                     {

                                         t.ParagraphSpacing(5, Unit.Millimetre);
                                         t.Span(request.Description).FontSize(14);
                                         t.EmptyLine();

                                     });

                                 });


                             x.Item().Border(1).Table(t =>
                             {

                                 t.ColumnsDefinition(h =>
                                 {

                                     h.ConstantColumn(30);
                                     h.ConstantColumn(60);
                                     h.ConstantColumn(60);
                                     h.RelativeColumn();
                                     h.ConstantColumn(80);
                                 });
                                 t.Header(h =>
                                 {

                                     h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text("م").Bold();
                                     h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text("كود تجارة").Bold();
                                     h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text("كود طب").Bold();
                                     h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text("الاسم").Bold();
                                     h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text("المبلغ").Bold();
                                 });

                                 decimal total = 0;
                                 uint row = 0;
                                 for (int r = 1; r < 4; r++)
                                 {


                                     t.Cell().Row((uint)r).Column(1).Border(1).Padding(2).AlignMiddle().AlignCenter().Text((r).ToString());
                                     t.Cell().Row((uint)r).Column(2).Border(1).Padding(2).AlignMiddle().AlignCenter().Text("9302");
                                     t.Cell().Row((uint)r).Column(3).Border(1).Padding(2).AlignMiddle().AlignCenter().Text("2308");
                                     t.Cell().Row((uint)r).Column(4).Border(1).Padding(2).AlignMiddle().AlignRight().Text("محمود محمد عبد الحميد");
                                     t.Cell().Row((uint)r).Column(5).Border(1).Padding(2).AlignMiddle().AlignCenter().Text("1300.25");
                                     total += 1300.25m;
                                     row = (uint)r;
                                 }

                                 t.Cell().Row((row + 1)).ColumnSpan(4).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text("المبلغ الاجمالى").Bold();
                                 t.Cell().Row((row + 1)).Column(5).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(total.ToString()).Bold();
                             });

                             if (!string.IsNullOrEmpty(request.Description))
                                 x.Item().Text(t =>
                                 {
                                     t.EmptyLine();
                                     t.AlignCenter();
                                     t.Span("و تفضلوا سيادتكم بقبول وافر الشكر و الاحترام").FontSize(14).FontFamily("Andalus").Bold();
                                 });
                         }

                         )

                         ;
                     p.Background()
                     .AlignBottom()
                     .Image("../Api/Content/logo3.png");
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
                                // g.Item(3).AlignRight().Text(t => t.Span("الموظف المختص"));
                                // g.Item(3).AlignCenter().Text(t => t.Span("رئيس القسم"));
                                // g.Item(3).AlignLeft().Text(t => t.Span(" رئيس المصلحة"));

                                t.Cell().Row(2).Column(2).AlignCenter().Text(x =>
                                {
                                    x.EmptyLine();
                                    x.EmptyLine();
                                    x.Span("-").FontSize(12).FontColor("#484848");

                                    x.CurrentPageNumber().FontSize(12).FontColor("#484848");
                                    x.Span("-").FontSize(12).FontColor("#484848");
                                });
                            })
                        ;


                 });
             }).WithMetadata(new DocumentMetadata()
             {
                 Title = "Report",
                 Author = "mohamed",
                 Subject = "hello",
                 Keywords = "Test",

             })
             .WithSettings(new DocumentSettings()
             {

             })
             .GeneratePdf();


            return await Task.FromResult(pdf);

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
                    NationalId = x.Employee.NationalId,
                    Amount = x.Amount,
                    EmployeeId = x.EmployeeId
                }).ToList(),
                Count = request.FormDetails.Count(),
                DailyId = request.DailyId,
                TotalAmount = request.FormDetails.Sum(x => x.Amount)


            };
            return await PrintPdf(formToReturn);
        }


        // public async Task<byte[]> PrintFormWithDetailsPdf_abandon(int formId)
        // {

        //     var request = await formRepository.GetFormWithDetailsByIdAsync(formId);

        //     foreach (var form in request.FormDetails)
        //     {
        //         form.Employee = await _employeeRepository.GetById(form.EmployeeId);
        //     }
        //     var totalText = NumericToLiteral.Convert(request.FormDetails.Sum(x => x.Amount), false, "جنيه", "جنيهات");
        //     totalText = totalText.Replace("(", "");
        //     totalText = totalText.Replace(")", "");
        //     totalText = totalText.Replace("،", "");

        //     var pdf = QuestPDF.Fluent.Document.Create(c =>
        //      {
        //          c.Page(p =>
        //          {
        //              p.Foreground().PaddingTop(10).PaddingBottom(10).PaddingRight(10).PaddingLeft(10).Border(3).BorderColor("#444444");
        //              p.ContentFromRightToLeft();
        //              p.DefaultTextStyle(TextStyle.Default.FontFamily("Arial"));
        //              p.Size(PageSizes.A4);
        //              p.Header().DefaultTextStyle(TextStyle.Default.FontFamily("Cairo"));
        //              p.Header().PaddingTop(0, Unit.Centimetre);

        //              p.Header().ScaleHorizontal(1.5f).ScaleVertical(.8f).AlignCenter().Column(c =>
        //              {
        //                  c.Item().Row(r =>
        //                  {
        //                      r.AutoItem().AlignCenter().Width(8, Unit.Centimetre).Height(4, Unit.Centimetre).Image("../Api/Content/images.png");
        //                  });
        //                  if (!string.IsNullOrEmpty(request.Description))
        //                      c.Item().Column(c =>
        //                      {
        //                          c.Item().ShowOnce().LineHorizontal(1).LineColor(Colors.Black);
        //                      });
        //              });

        //              p.Margin(15, QuestPDF.Infrastructure.Unit.Millimetre);
        //              p.PageColor(Colors.White);

        //              p.Content()

        //                  .PaddingVertical(1, QuestPDF.Infrastructure.Unit.Millimetre)
        //                  .ContentFromRightToLeft()
        //                  .Column(x =>

        //                  {
        //                      if (!string.IsNullOrEmpty(request.Description))
        //                          x.Item().Row(r =>
        //                          {
        //                              r.RelativeItem().AlignRight().Text(t =>
        //                              {
        //                                  t.Span(" السيد الاستاذ الدكتور / عميد كليه الطب البشرى").FontSize(18).FontFamily("Andalus").Underline().ExtraBold();
        //                                  t.EmptyLine();
        //                              });
        //                          });
        //                      if (!string.IsNullOrEmpty(request.Description))
        //                          x.Item().Row(r2 =>
        //                          {

        //                              r2.RelativeItem().AlignCenter().Text(t =>
        //                          {
        //                              t.Span("تحيه طيبه و بعد ،،،،").Bold().FontSize(14);
        //                              t.EmptyLine();
        //                          });
        //                          });
        //                      if (!string.IsNullOrEmpty(request.Description))
        //                          x.Item().Row(r3 =>
        //                          {
        //                              r3.RelativeItem().AlignRight().Text(t =>
        //                              {

        //                                  t.ParagraphSpacing(1, Unit.Millimetre);
        //                                  t.Span(request.Description).FontSize(14);
        //                                  t.EmptyLine();

        //                              });

        //                          });


        //                      x.Item().Border(1).Table(async t =>
        //                      {

        //                          t.ColumnsDefinition(h =>
        //                          {

        //                              h.ConstantColumn(35);
        //                              h.ConstantColumn(60);
        //                              h.ConstantColumn(60);
        //                              h.RelativeColumn();
        //                              h.ConstantColumn(80);
        //                          });
        //                          t.Header(h =>
        //                          {

        //                              h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
        //                                 t =>
        //                                 {
        //                                     t.Span("م").Bold().FontFamily("Cairo").FontSize(10);
        //                                 }


        //                                 );
        //                              h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
        //                                    t =>
        //                                 {
        //                                     t.Span("كود تجارة").Bold().FontFamily("Cairo").FontSize(10);
        //                                 });
        //                              h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
        //                                 {
        //                                     t.Span("كود طب").Bold().FontFamily("Cairo").FontSize(10);
        //                                 });
        //                              h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
        //                                 {
        //                                     t.Span("الاسم").Bold().FontFamily("Cairo").FontSize(10);
        //                                 });
        //                              h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
        //                                 {
        //                                     t.Span("المبلغ").Bold().FontFamily("Cairo").FontSize(10);
        //                                 });
        //                          });

        //                          //decimal total = 0;
        //                          uint row = 0;

        //                          foreach (var form in request.FormDetails)
        //                          {


        //                              t.Cell().Row((uint)row + 1).Column(1).Border(1).Padding(2).AlignMiddle().AlignCenter().Text((row + 1).ToString());
        //                              t.Cell().Row((uint)row + 1).Column(2).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Employee.TegaraCode.ToString());
        //                              t.Cell().Row((uint)row + 1).Column(3).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Employee.TabCode.ToString());
        //                              t.Cell().Row((uint)row + 1).Column(4).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Employee.Name);
        //                              t.Cell().Row((uint)row + 1).Column(5).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Amount.ToString());
        //                              row++;
        //                          }



        //                          t.Cell().Row((row + 1)).ColumnSpan(4).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(
        //                             t =>
        //                             {
        //                                 t.Span(" اجمالى المبلغ : " + totalText + "فقط لا غير").Bold().FontSize(8).FontFamily("Cairo");
        //                             });


        //                          t.Cell().Row((row + 1)).Column(5).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(request.FormDetails.Sum(x => x.Amount).ToString()).Bold();

        //                      });

        //                      if (!string.IsNullOrEmpty(request.Description))
        //                          //  x.Item().Text(t =>
        //                          //     {
        //                          //         t.AlignRight();
        //                          //         t.Span("إجمالى  المبلغ :" + totalText + " فقط لا غير ")
        //                          //         .FontSize(12).FontFamily("Arial").Bold();
        //                          //     });

        //                          x.Item().Text(t =>
        //                          {
        //                              t.EmptyLine();
        //                              t.AlignCenter();
        //                              t.Span("و تفضلوا سيادتكم بقبول وافر الشكر و الاحترام").FontSize(16).FontFamily("Andalus").Bold();
        //                          });
        //                  }

        //                  )

        //                  ;
        //              p.Background()
        //              .AlignBottom()
        //              .Image("../Api/Content/logo3.png");
        //              p.Footer()
        //                     .Table(t =>
        //                     {
        //                         t.ColumnsDefinition(c =>
        //                         {
        //                             c.RelativeColumn();
        //                             c.RelativeColumn();
        //                             c.RelativeColumn();
        //                         });

        //                         t.Cell().Row(1).Column(1).AlignRight().Text("الموظف المختص").Bold();
        //                         t.Cell().Row(1).Column(2).AlignCenter().Text("رئيس القسم").Bold();
        //                         t.Cell().Row(1).Column(3).AlignLeft().Text("رئيس المصلحة").Bold();


        //                         t.Cell().Row(2).Column(2).AlignCenter().Text(x =>
        //                         {
        //                             x.EmptyLine();
        //                             x.EmptyLine();
        //                             x.Span("-").FontSize(12).FontColor("#484848");

        //                             x.CurrentPageNumber().FontSize(12).FontColor("#484848");
        //                             x.Span("-").FontSize(12).FontColor("#484848");
        //                         });
        //                     });
        //          });
        //      }).WithMetadata(new DocumentMetadata()
        //      {
        //          Title = "Report",
        //          Author = "mohamed",
        //          Subject = "hello",
        //          Keywords = "Test",

        //      })
        //      .WithSettings(new DocumentSettings()
        //      {

        //      })
        //      .GeneratePdf();


        //     return await Task.FromResult(pdf);

        // }






        public async Task<byte[]> PrintPdf(FormDto formModel)
        {

            // var request = await formRepository.GetFormWithDetailsByIdAsync(formModel.Id);

            // foreach (var form in request.FormDetails)
            // {
            //     form.Employee = await _employeeRepository.GetById(form.EmployeeId);
            // }
            var totalText = NumericToLiteral.Convert(formModel.FormDetails.Sum(x => x.Amount), false, "جنيه", "جنيهات");
            totalText = totalText.Replace("(", "");
            totalText = totalText.Replace(")", "");
            totalText = totalText.Replace("،", "");

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
                             r.AutoItem().AlignCenter().Width(8, Unit.Centimetre).Height(4, Unit.Centimetre).Image("../Api/Content/images.png");
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


                             x.Item().Border(1).Table(async t =>
                             {

                                 t.ColumnsDefinition(h =>
                                 {

                                     h.ConstantColumn(35);
                                     h.ConstantColumn(60);
                                     h.ConstantColumn(60);
                                     h.RelativeColumn();
                                     h.ConstantColumn(80);
                                 });
                                 t.Header(h =>
                                 {

                                     h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(
                                        t =>
                                        {
                                            t.Span("م").Bold().FontFamily("Cairo").FontSize(10);
                                        }


                                        );
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
                                            t.Span("الاسم").Bold().FontFamily("Cairo").FontSize(10);
                                        });
                                     h.Cell().Border(1).Background("#b8b8b8").AlignCenter().Height(1, Unit.Centimetre).AlignMiddle().Text(t =>
                                        {
                                            t.Span("المبلغ").Bold().FontFamily("Cairo").FontSize(10);
                                        });
                                 });

                                 //decimal total = 0;
                                 uint row = 0;

                                 foreach (var form in formModel.FormDetails)
                                 {


                                     t.Cell().Row((uint)row + 1).Column(1).Border(1).Padding(2).AlignMiddle().AlignCenter().Text((row + 1).ToString());
                                     t.Cell().Row((uint)row + 1).Column(2).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.TegaraCode.ToString());
                                     t.Cell().Row((uint)row + 1).Column(3).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.TabCode.ToString());
                                     t.Cell().Row((uint)row + 1).Column(4).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Name);
                                     t.Cell().Row((uint)row + 1).Column(5).Border(1).Padding(2).AlignMiddle().AlignCenter().Text(form.Amount.ToString());
                                     row++;
                                 }



                                 t.Cell().Row((row + 1)).ColumnSpan(4).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(
                                    t =>
                                    {
                                        t.Span(" اجمالى المبلغ : " + totalText + "فقط لا غير").Bold().FontSize(8).FontFamily("Cairo");
                                    });


                                 t.Cell().Row((row + 1)).Column(5).Background("#b8b8b8").Border(1).AlignCenter().Padding(4).Text(formModel.FormDetails.Sum(x => x.Amount).ToString()).Bold();

                             });

                             if (!string.IsNullOrEmpty(formModel.Description))
                                 //  x.Item().Text(t =>
                                 //     {
                                 //         t.AlignRight();
                                 //         t.Span("إجمالى  المبلغ :" + totalText + " فقط لا غير ")
                                 //         .FontSize(12).FontFamily("Arial").Bold();
                                 //     });

                                 x.Item().Text(t =>
                                 {
                                     t.EmptyLine();
                                     t.AlignCenter();
                                     t.Span("و تفضلوا سيادتكم بقبول وافر الشكر و الاحترام").FontSize(16).FontFamily("Andalus").Bold();
                                 });
                         }

                         )

                         ;
                     p.Background()
                     .AlignBottom()
                     .Image("../Api/Content/logo3.png");
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
                 Keywords = "Test",

             })
             .WithSettings(new DocumentSettings()
             {

             })
             .GeneratePdf();


            return await Task.FromResult(pdf);

        }


    }
}