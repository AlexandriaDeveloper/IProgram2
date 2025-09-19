import { Param } from "./Param";


export interface IForm {
  id?: number;
  index: number;

  name: string;
  description?: string;
  dailyId?: number;
}
export class FormParam extends Param {
  id?: number;
  index: number;
  name: string;
  createdBy: string;
  dailyId?: number;
}


