import { Param } from './../../shared/models/Param';
import { ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { FormArray, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ReportpdfService } from '../../shared/service/reportpdf.service';
import { EmployeeService } from '../../shared/service/employee.service';
import { MatDialog } from '@angular/material/dialog';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable } from '@angular/material/table';
import { DailyParam } from '../../shared/models/IDaily';
import { IEmployee } from '../../shared/models/IEmployee';
import { DailyService } from '../../shared/service/daily.service';
import { FormService } from '../../shared/service/form.service';
import { ActivatedRoute } from '@angular/router';
import { AddFormComponent } from './add-form/add-form.component';
import { FormParam } from '../../shared/models/IForm';
import { AddEmployeeDialogComponent } from './form-details/add-employee-dialog/add-employee-dialog.component';

@Component({
  selector: 'app-form',
  standalone: false,

  templateUrl: './form.component.html',
  styleUrl: './form.component.scss'
})
export class FormComponent implements OnInit {

  //dailyId;
  dialog =inject(MatDialog)
  displayedColumns = ['action','name','count','total'];
  formService = inject(FormService);
  dailyId = inject(ActivatedRoute).snapshot.params['id'];
  public param :   FormParam=new FormParam();
  dataSource;
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;
  @ViewChild("nameInput") nameInput :ElementRef;
  @ViewChild("countInput") countInput :ElementRef;
  @ViewChild("totalInput") totalInput :ElementRef;
  //@ViewChild("dateInput") dateInput ;

pdfReportService=inject(ReportpdfService);
employeeService = inject(EmployeeService)
form :FormGroup
fb =inject(FormBuilder);
  constructor(private cdref: ChangeDetectorRef) {

  }
  ngOnInit(): void {
console.log(this.dailyId);

   this.form=this.initilizeForm();
   this.loadData();
   this.cdref.detectChanges();
  }


  initilizeForm(){
  return this.fb.group({
    description:[],
    formDetails:this.fb.array([])
    })
  }



  onSubmit(){

  }
  loadData(){
    console.log(this.dailyId);

    this.formService.GetForms(this.dailyId,this.param).subscribe({
      next:(x:any)=>{
        this.dataSource=x.data
        this.table.dataSource=x.data
        this.paginator.length=x.count;
        this.table.renderRows();
      }
    })
  }
  onChange(ev){
    this.param.pageSize=ev.pageSize;
    this.param.pageIndex=ev.pageIndex;
    this.loadData();
  }
  openDialog(model): void {

    const dialogRef = this.dialog.open(AddFormComponent, {
     // data: {name: this.name, animal: this.animal},
      width: '40%',

     disableClose: true,
      data:  {  form : model,dailyId :this.dailyId}


    });

    dialogRef.afterClosed().subscribe(result => {
      this.loadData();
     // this.animal = result;
    });
  }
  onDelete(row){
  if(  confirm(` أنت على وشك حذف ملف ${row.name} هل انت متاكد ؟؟!`)){
    this.formService.deleteForm(row.id).subscribe({
      next:(x:any)=>{
        this.loadData();
      }
    })
  }
  }
  exportPdf(){
    this.formService.exportFormsInsidDaily(this.dailyId).subscribe({
      next:(x:any)=>{
        console.log(x);
      }
    })
  }


}





