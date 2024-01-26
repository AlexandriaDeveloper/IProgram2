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
import { fromEvent, debounceTime, distinctUntilChanged, map } from 'rxjs';
import { FormService } from '../../../shared/service/form.service';
import { ToasterService } from '../../../shared/components/toaster/toaster.service';
import { ReferencesDialogComponent } from './references-dialog/references-dialog.component';
import { UploadReferencesDialogComponent } from './upload-references-dialog/upload-references-dialog.component';


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
  router =inject(Router)
  id = inject(ActivatedRoute).snapshot.params['formid']
  dailyId = inject(ActivatedRoute).snapshot.params['id']
   toasterService = inject(ToasterService);
  data :any;
  dataSource;
  filteredData :IEmployee[]=[]
  displayedColumns = ['action','tabCode','tegaraCode','name','nationalId','amount']
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;

  @ViewChild("tabCodeInput") tabCodeInput :ElementRef;
  @ViewChild("tegaraCodeInput") tegaraCodeInput :ElementRef;
  @ViewChild("nameInput") nameInput :ElementRef;
  @ViewChild("nationalIdInput") nationalIdInput :ElementRef;
  @ViewChild("amountInput") amountInput :ElementRef;
  ngOnInit(): void {
  this.loadData();
  }
  ngAfterViewInit(): void {
    this.search();
  }
  search(){
    this.initElement(this.tabCodeInput,'tabCode');
    this.initElement(this.tegaraCodeInput,'tegaraCode');
    this.initElement(this.nameInput,'name');
    this.initElement(this.nationalIdInput,'nationalId');
    this.initElement(this.amountInput,'amount');
  }
  initElement(element :ElementRef,param ){


    fromEvent(element.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
    map((event: any) => {
      this.dataSource=this.data.formDetails;

      if(event.target.value==''){
        return event.target.value
      }

      this.dataSource=this.data.formDetails.filter(x=>{
        if(param === 'name'||param === 'nationalId'){
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
        console.log(x);

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
    this.formDetailsService.exportForms(this.id).subscribe(x=>console.log(x))
  }
  drop(event){
    console.log(this.dataSource);

  const previousIndex = this.dataSource.findIndex(row => row === event.item.data);
  moveItemInArray(this.dataSource,previousIndex, event.currentIndex);
  this.reOrderRows();
  this.table.renderRows();

  }
  reOrderRows(){
    var ids =this.dataSource.map(x=>x.id)
    this.formDetailsService.reOrderRows(this.id,ids).subscribe(x=>console.log(x))
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
    if(input=='nationalId'){
      this.nationalIdInput.nativeElement.value=''
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





}
