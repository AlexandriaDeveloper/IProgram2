import { EmployeeParam } from '../../shared/models/IEmployee';
import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild, inject } from '@angular/core';
import { MatTableModule, MatTable } from '@angular/material/table';
import { MatPaginatorModule, MatPaginator } from '@angular/material/paginator';
import { MatSortModule, MatSort } from '@angular/material/sort';

import { EmployeeService } from '../../shared/service/employee.service';
import { IEmployee } from '../../shared/models/IEmployee';
import { debounceTime, distinctUntilChanged, fromEvent, map, of } from 'rxjs';
import { ActivatedRoute } from '@angular/router';

@Component({
  selector: 'app-list',
  templateUrl: './list.component.html',
  styleUrls: ['./list.component.scss'],
  standalone: false,

})
export class ListComponent implements AfterViewInit,OnInit {
  employeeService = inject(EmployeeService);
  router = inject(ActivatedRoute);
  public param :   EmployeeParam=new EmployeeParam();
  @ViewChild(MatPaginator) paginator!: MatPaginator;
  @ViewChild(MatSort) sort!: MatSort;
  @ViewChild(MatTable) table!: MatTable<IEmployee>;
  @ViewChild("tabCodeInput") tabCodeInput :ElementRef;
  @ViewChild("tegaraCodeInput") tegaraCodeInput :ElementRef;
  @ViewChild("nameInput") nameInput :ElementRef;
  @ViewChild("nationalIdInput") nationalIdInput :ElementRef;
  @ViewChild("collageInput") collageInput :ElementRef;

  dataSource ;

constructor( private cdref: ChangeDetectorRef ) {}
  ngOnInit(): void {
    // console.log('onInit');

    if(this.router.snapshot.queryParams['departmentId']){

      this.param.departmentId=this.router.snapshot.queryParams['departmentId']
    }
    else{

      this.param=new EmployeeParam();
    }
    this.loadData();
    this.cdref.detectChanges();
  }
  ngAfterViewInit(): void {

    this.search();
  }
  search(){
    this.initElement(this.tabCodeInput,'tabCode');
    this.initElement(this.tegaraCodeInput,'tegaraCode');
    this.initElement(this.nameInput,'name');
    this.initElement(this.nationalIdInput,'nationalId');
    this.initElement(this.collageInput,'collage');
  }
  initElement(element :ElementRef,param ){


    fromEvent(element.nativeElement, 'keyup').pipe(debounceTime(600), distinctUntilChanged(),
    map((event: any) => {
    return event.target.value;
    })
    ).subscribe(x=>{
        switch(param){
          case 'tabCode':
          this.param.tabCode=x; break;
          case 'tegaraCode':
          this.param.tegaraCode=x; break;
          case 'name':
          this.param.name=x; break;
          case 'nationalId':
          this.param.nationalId=x; break;
          case 'collage':
          this.param.collage=x; break;
        }
        this.loadData();
    })
  }



  /** Columns displayed in the table. Columns IDs can be added, removed, or reordered. */
  displayedColumns = ['action','tabCode','tegaraCode', 'name','nationalId','collage'];



  loadData(): void {



  this.employeeService.GetEmployees(this.param).subscribe((x:any) =>{
    this.dataSource=x.data
    this.paginator.length=x.count;
    // this.paginator.pageIndex=x.pageIndex;
    // this.paginator.pageSize=x.pageSize;
  });


  }
  onChange(ev){
    this.param.pageSize=ev.pageSize;
    this.param.pageIndex=ev.pageIndex;


    this.loadData();

  }
  editEmployee(id:number){
    // console.log(id);
  }
  deleteEmployee(id:number){
    // console.log(id);
  }
  clear(input:any){


    if(input==='tabCode'){
      this.tabCodeInput.nativeElement.value = '';
      this.param.tabCode=null;
    }
    if(input==='tegaraCode'){
      this.tegaraCodeInput.nativeElement.value = '';
      this.param.tegaraCode=null;
    }
    if(input==='name'){
      this.nameInput.nativeElement.value = '';
      this.param.name=null;
    }
    if(input==='nationalId'){
      this.nationalIdInput.nativeElement.value = '';
      this.param.nationalId=null;
    }
    if(input==='collage'){
      this.collageInput.nativeElement.value = '';
      this.param.collage=null;
    }
      this.loadData();

  }
}
