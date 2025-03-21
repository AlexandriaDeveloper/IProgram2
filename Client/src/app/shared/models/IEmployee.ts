import { Param } from "./Param"

export interface IEmployee {
  id?: string;
  name: string
  tabCode: number
  tegaraCode: number

  collage: string
  departmentId?: number
  departmentName?: string
  email?: string
  section?: string
  employeeRefernces?: any[]
  hasReferences?: boolean
  bankInfo?: IEmployeeBanKInfo | null
}
export interface IEmployeeBanKInfo {
  bankName?: string
  accountNumber?: string
  branchName?: string
  employeeId?: number

}

export interface IUploadEmployee {
  file: File
}

export class EmployeeParam extends Param {
  id: string
  name: string
  tabCode: number
  tegaraCode: number
  collage: string
  departmentId?: number
  departmentName?: string


}

export interface IEmployeeSearch {

  employeeId: string;
  tabCode: number;
  tegaraCode: number;
}

export class EmployeeReportRequest {
  employeeId: string;
  startDate?: string;
  endDate?: string;
}

export class EmployeeDownloadParam {
  departmentId?: string;
  collage?: string;
  section?: string;


}