import { Param } from "./Param";


export interface IForm {
  id?: number;
  name: string;
  dailyId?:number;
}
export class FormParam extends Param{
  id ?:number;
  name : string;
  dailyId?:number;
}


