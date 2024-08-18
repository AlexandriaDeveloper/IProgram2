import { ActivatedRoute, Router } from '@angular/router';
import { FormDetailsService } from './../../../shared/service/form-details.service';
import { AfterViewInit, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { DescriptionDialogComponent } from './description-dialog/description-dialog.component';
import { MatDialog } from '@angular/material/dialog';
import { MatPaginator } from '@angular/material/paginator';
import { MatSort } from '@angular/material/sort';
import { MatTable } from '@angular/material/table';
import { IEmployee } from '../../../shared/models/IEmployee';
import { AddEmployeeDialogComponent } from './add-employee-dialog/add-employee-dialog.component';
import { moveItemInArray } from '@angular/cdk/drag-drop';
import { fromEvent, debounceTime, distinctUntilChanged, map, Subscription } from 'rxjs';
import { FormService } from '../../../shared/service/form.service';
import { ToasterService } from '../../../shared/components/toaster/toaster.service';
import { ReferencesDialogComponent } from './references-dialog/references-dialog.component';
import { UploadReferencesDialogComponent } from './upload-references-dialog/upload-references-dialog.component';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { UploadExcelFileBottomComponent } from './upload-excel-file-bottom/upload-excel-file-bottom.component';
import { IDaily } from '../../../shared/models/IDaily';
import { DailyService } from '../../../shared/service/daily.service';



@Component({
  selector: 'app-form-details',
  standalone: false,
  templateUrl: './form-details.component.html',
  styleUrl: './form-details.component.scss'
})
export class FormDetailsComponent implements OnInit  ,AfterViewInit{

  dialog =inject(MatDialog)
  formDetailsService= inject(FormDetailsService);
  formService= inject(FormService);
  dailyService= inject(DailyService);
  router =inject(Router)
  id = inject(ActivatedRoute).snapshot.params['formid']
  dailyId = inject(ActivatedRoute).snapshot.params['id']
  daily : IDaily;
   toasterService = inject(ToasterService);
   bottomSheet =inject(MatBottomSheet)
  data :any;
  dataSource;
  filteredData :IEmployee[]=[]
  displayedColumns = ['action','tabCode','tegaraCode','name','department','employeeId','amount']
  isLoading =false;

  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;

  @ViewChild("tabCodeInput") tabCodeInput :ElementRef;
  @ViewChild("tegaraCodeInput") tegaraCodeInput :ElementRef;
  @ViewChild("nameInput") nameInput :ElementRef;
  @ViewChild("departmentInput") departmentInput :ElementRef;
  @ViewChild("employeeIdInput") employeeIdInput :ElementRef;
  @ViewChild("amountInput") amountInput :ElementRef;


 tabSub :Subscription;

  ngOnInit(): void {
    this.loadDaily();
  this.loadData();
  }
  loadDaily() {
    if(this.dailyId!==undefined){
      this.dailyService.getDaily(this.dailyId).subscribe(x => {
        console.log(x);

        this.daily = x
      })
    }
    else{
      this.daily={
        id:null,
        dailyDate:new Date(),
        name:'ارشيف',

      }
    }

  }
  ngAfterViewInit(): void {
    this.search();
  }
  search(){
    this.initElement(this.tabCodeInput,'tabCode');
    this.initElement(this.tegaraCodeInput,'tegaraCode');
    this.initElement(this.nameInput,'name');
    this.initElement(this.employeeIdInput,'employeeId');
    this.initElement(this.amountInput,'amount');
    this.initElement(this.departmentInput,'department');

  }
  initElement(element :ElementRef,param ){


  return  fromEvent(element.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
    map((event: any) => {
      console.log(this.dataSource);

      this.dataSource=this.data.formDetails;

      if(event.target.value==''){
        return event.target.value
      }

      this.dataSource=this.data.formDetails.filter(x=>{
        console.log(param);

        if(param === 'name'||param === 'employeeId'||param === 'department'){
          if(x[param]!== null)
          return x[param].includes(event.target.value);
        }
        else{
          return x[param]==(event.target.value.toString());
        }


       // return x[param].includes(event.target.value)
      });


    return event.target.value;
    })
    ).subscribe();
  }


  loadData(){

    this.formDetailsService.GetFormDetails(this.id).subscribe(x=>
      {
        // console.log(x);

        this.data=x;
       // this.paginator.length=x.formDetails.length;
        this.dataSource=x.formDetails
      })
  }
  onChange(ev){

  }


  openDescriptionDialog(){

    const dialogRef = this.dialog.open(DescriptionDialogComponent, {
       data: {form :this.data},
       width: '80%',
        height:'600px',
      disableClose: true,



     });

     dialogRef.afterClosed().subscribe(result => {
       this.loadData();
     });

  }

  openReferenceDialog(){

    const dialogRef = this.dialog.open(ReferencesDialogComponent, {
       data: {formId :this.data.id},
       width: '80%',
        height:'610px',
      disableClose: true,
      panelClass:['dialog-container'],
     });

     dialogRef.afterClosed().subscribe(result => {
       this.loadData();
     });

  }
  uploadReferenceDialog(){
    const dialogRef = this.dialog.open(UploadReferencesDialogComponent, {
      // data: {name: this.name, animal: this.animal},


      disableClose: true,
       data:  { formId :this.id },
      panelClass: ['dialog-container'],


     });

     dialogRef.afterClosed().subscribe(result => {
       this.ngOnInit();
      // this.animal = result;
     });
  }


  openUploadExcelBottomSheet() {
    this.bottomSheet.open(UploadExcelFileBottomComponent, {
      panelClass: ['bottomSheet'],
      hasBackdrop:true,
      data: {
        formId :this.id
      //  departmentId:this.param.departmentId
      }


    });

    this.bottomSheet._openedBottomSheetRef.afterDismissed().subscribe(result => {
      if(result){
      this.loadData();
     // this.toaster.openSuccessToaster('تم رفع الملف  بنجاح','check_circle');
      }
    })
    }
  addEmployeeFormDialog(model){
    const dialogRef = this.dialog.open(AddEmployeeDialogComponent,{
      width: '50%',
      data:{employeeDetails :model,formId:this.id},
      disableClose: true,
    })
    dialogRef.afterClosed().subscribe(result => {
    this.loadData();
     // this.animal = result;
    });
  }
  deleteEmployee(row){
    if(confirm(`انت على وشك حذف الموظف ${row.name} هل انت متاكد ؟؟!`)){
      this.formDetailsService.deleteFormDetails(row.id).subscribe(x=>this.loadData())
    }
   // this.formDetailsService.deleteFormDetails(rowId).subscribe(x=>this.loadData())
  }
  exportPdf(){
    this.formDetailsService.exportForms(this.id).subscribe()
  }
  drop(event){
    if(this.daily.closed){
      return;
    }
    // console.log(this.dataSource);

  const previousIndex = this.dataSource.findIndex(row => row === event.item.data);
  moveItemInArray(this.dataSource,previousIndex, event.currentIndex);
  this.reOrderRows();
  this.table.renderRows();

  }
  reOrderRows(){
    var ids =this.dataSource.map(x=>x.id)
    this.formDetailsService.reOrderRows(this.id,ids).subscribe()
  }
  clear(input){
    if(input=='tabCode'){
      this.tabCodeInput.nativeElement.value=''
    }
    if(input=='tegaraCode'){
      this.tegaraCodeInput.nativeElement.value=''
    }
    if(input=='name'){
      this.nameInput.nativeElement.value=''
    }
    if(input=='department'){
      this.departmentInput.nativeElement.value=''
    }
    if(input=='employeeId'){
      this.employeeIdInput.nativeElement.value=''
    }
    if(input=='amount'){
      this.amountInput.nativeElement.value=''
    }
    this.dataSource=this.data.formDetails;
  }
  moveFromDailyToArchive(){
    if(confirm(`هل تريد الغاء استمارة ${this.data.name} ؟من اليوميه!`)){

      this.formService.moveFormDetailsDailyArchive({formId:this.id, dailyId:null}).subscribe(x=>{
        this.router.navigateByUrl(`/daily/${this.dailyId}/form`);
      });

    }
  }
  copyFormToArchive(){

    this.formService.CopyFormToArchive(this.id).subscribe({
      next:(x:any)=>{
       this.toasterService.openSuccessToaster('تم نسخ النموذج بنجاح')
      }
    })
  }
  downloadExcel(){
    this.isLoading=true
    this.formService.downloadExcelForm({formId: this.id,formTitle : this.data.name}).subscribe(
      {
        complete: () => {
          this.isLoading=false
        }
      }

    )
  }




}
