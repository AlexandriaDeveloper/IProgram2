import { AfterViewInit, Component, Input, OnInit, inject } from '@angular/core';
import { EmployeeReportRequest, IEmployee } from '../../../../shared/models/IEmployee';
import { ReportpdfService } from '../../../../shared/service/reportpdf.service';
import { EmployeeService } from '../../../../shared/service/employee.service';
import moment from 'moment';

@Component({
  selector: 'app-employee-report',
  standalone: false,
  templateUrl: './employee-report.component.html',
  styleUrl: './employee-report.component.scss'
})
export class EmployeeReportComponent implements OnInit ,AfterViewInit{
  employeeDailies ;
  request =new EmployeeReportRequest();
  ngOnInit(): void {
    console.log(this.employee);

    this.request.employeeId=this.employee.id

   this.load();
  }
  ngAfterViewInit(): void {
   // throw new Error('Method not implemented.');
  }
  @Input("employee") employee:IEmployee;

  employeeService =inject(EmployeeService)
  panelOpenState = false;
  reportService = inject(ReportpdfService);

  printPdf(){



   this.reportService.employeePdfReport(this.request).subscribe((x:any)=>{
    // // console.log(x.text);

   //window.open(x.text,'_blank')

   })
   }
   onStartChange(event){
    // console.log(event );

    this.request.startDate=moment( event.value).toISOString();

  }
   onEndChange(event){
     // console.log(event);

     this.request.endDate=moment( event.value).toISOString();

   }
   load(){


    this.employeeService.employeeReport(this.request).subscribe(x=>{
       // console.log(x);
       this.employeeDailies=x;

    })
   }
   search(){
    // console.log(this.request);

     this.load();
   }
   clearEnd (){

    this.request.endDate= null;
   }
   clearStart (){
    this.request.startDate=null;
   }
}
