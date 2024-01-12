import { Param } from "./Param";


export interface IDaily{
  id ?:number;
  name : string;
  dailyDate:Date;
}
export class DailyParam extends Param{
  id ?:number;
  name : string;
  startDate:string;
  endDate:string;
  closed?:boolean;
}

export class IFormDetailsEmployee {
  employeeId:number;

  amount : number
}






