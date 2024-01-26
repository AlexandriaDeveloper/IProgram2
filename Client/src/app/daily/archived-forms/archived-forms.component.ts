import { ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { FormGroup, FormBuilder } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable } from '@angular/material/table';
import { ActivatedRoute } from '@angular/router';
import { IEmployee } from '../../shared/models/IEmployee';
import { FormParam } from '../../shared/models/IForm';
import { EmployeeService } from '../../shared/service/employee.service';
import { FormService } from '../../shared/service/form.service';
import { ReportpdfService } from '../../shared/service/reportpdf.service';
import { AddFormComponent } from '../form/add-form/add-form.component';
import { FormArchivedService } from '../../shared/service/form-archived.service';
import { ToasterService } from '../../shared/components/toaster/toaster.service';
import { MoveToDailyDialogComponent } from './move-to-daily-dialog/move-to-daily-dialog.component';

@Component({
  selector: 'app-archived-forms',
  standalone: false,

  templateUrl: './archived-forms.component.html',
  styleUrl: './archived-forms.component.scss'
})
export class ArchivedFormsComponent implements OnInit {
  //dailyId;
  dialog =inject(MatDialog)
  displayedColumns = ['action','name','count','total'];
 // formService = inject(FormService);
  formArchiveService = inject(FormArchivedService);
  toaster = inject(ToasterService);
  //dailyId = inject(ActivatedRoute).snapshot.params['id'];
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
//console.log(this.dailyId);

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
  //  console.log(this.dailyId);

    this.formArchiveService.GetArchivedForms(this.param).subscribe({
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
  onArchive(row){
    console.log(row);
    row.checked=  !row.checked;

  }
  openDialog(model): void {

    const dialogRef = this.dialog.open(AddFormComponent, {
      width: '40%',
     disableClose: true,
      data:  {  form : model}
    });

    dialogRef.afterClosed().subscribe(result => {
      this.loadData();
    });
  }

  onDelete(row){
    if(  confirm(` أنت على وشك حذف ملف ${row.name} هل انت متاكد ؟؟!`)){
      this.formArchiveService.deleteForm(row.id).subscribe({
        next:(x:any)=>{
          this.loadData();
        }
      })
    }
  }
  deleteCheckedRows(){
   const ids= this.dataSource.filter(x=>x.checked).map(x=>x.id);
   console.log(ids);
   this.formArchiveService.deleteMultiForms(ids).subscribe(x=>{
  this.toaster.openSuccessToaster('تم حذف الملفات بنجاح','check')
    this.loadData()
  })}

  hasChecked(){
    var hasChecked = this.dataSource.filter(x=>{
      if(x?.checked!==undefined)
      return x?.checked
    }).length>0

    return hasChecked
  }
  openMoveToDailyDialog(): void {

    const dialogRef = this.dialog.open(MoveToDailyDialogComponent, {
      width: '40%',
      disableClose: true,

    });

    dialogRef.afterClosed().subscribe(result => {
      console.log(result);

this.formArchiveService.moveFormArchiveToDaily({ dailyId :result ,formIds:this.dataSource.filter(x=>x.checked).map(x=>x.id)}).subscribe({
  next:(x:any)=>{
    this.loadData();
  }
})

    });
  }
  info(){
     this.toaster.openInfoToaster('تم حذف الملفات بنجاح','check')
  }
  success(){
    this.toaster.openSuccessToaster('تم حذف الملفات بنجاح','check')
 }
 error(){
  this.toaster.openErrorToaster('تم حذف الملفات بنجاح','check')
}
clear(name){
  if(name==='name'){
    this.nameInput.nativeElement.value=''
    this.param.name=''
    this.loadData();
  }
}
}
