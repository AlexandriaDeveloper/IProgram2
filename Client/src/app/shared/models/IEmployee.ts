import { Param } from "./Param"

export interface IEmployee {
  id?:number;
  name: string
  tabCode : number
  tegaraCode : number
  nationalId:string
  collage:string
  departmentId? : number
}

export interface IUploadEmployee {
  file :File
}

export class EmployeeParam extends Param{

  name : string
  tabCode : number
  tegaraCode : number
  nationalId:string
  collage:string
  departmentId? : number


}

export interface IEmployeeSearch {

  nationalId :string;
  tabCode:number;
  tegaraCode:number;
}
