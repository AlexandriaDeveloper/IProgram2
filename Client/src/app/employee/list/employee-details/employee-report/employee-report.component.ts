import { Component, Input, inject } from '@angular/core';
import { EmployeeReportRequest, IEmployee } from '../../../../shared/models/IEmployee';
import { ReportpdfService } from '../../../../shared/service/reportpdf.service';

@Component({
  selector: 'app-employee-report',
  standalone: false,
  templateUrl: './employee-report.component.html',
  styleUrl: './employee-report.component.scss'
})
export class EmployeeReportComponent {
  @Input("employee") employee:IEmployee;

  reportService = inject(ReportpdfService);

  printPdf(){
    let request =new EmployeeReportRequest();
    request.id=this.employee.id
    request.startDate=new Date(1970,1,1).toISOString ()
    request.endDate=new Date(2025,1,1).toISOString ()

   this.reportService.employeePdfReport(request).subscribe((x:any)=>{
     console.log(x.text);

   //window.open(x.text,'_blank')

   })
   }
}
