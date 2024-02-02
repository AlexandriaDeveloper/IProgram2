import { Param } from "./Param"

export interface IEmployee {
  id?:number;
  name: string
  tabCode : number
  tegaraCode : number
  nationalId:string
  collage:string
  departmentId? : number
  departmentName?:string
  email ?: string
  section ?: string
  employeeRefernces?:any[]
  hasReferences?:boolean
  bankInfo?:IEmployeeBanKInfo |null
}
export interface IEmployeeBanKInfo{
  bankName?:string
  accountNumber?:string
  branchName? :string
  employeeId?:number

}

export interface IUploadEmployee {
  file :File
}

export class EmployeeParam extends Param{
  id:number
  name : string
  tabCode : number
  tegaraCode : number
  nationalId:string
  collage:string
  departmentId? : number
  departmentName?:string


}

export interface IEmployeeSearch {

  nationalId :string;
  tabCode:number;
  tegaraCode:number;
}

export class EmployeeReportRequest{
  id:number;
  startDate?:string;
  endDate?:string;
}
