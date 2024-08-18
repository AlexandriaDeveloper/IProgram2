import { Param } from "./Param";






export class DepartmentParam extends Param {
  id?: number;
  name: string;

}

export interface IDepartment {
  id?: number;
  name: string;
}


export interface Ids{
  ids:string[];
}
