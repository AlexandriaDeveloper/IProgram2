import { Injectable } from '@angular/core';

@Injectable({
  providedIn: 'root'
})
export class LoadingService {

  constructor() { }

  isLoading(){
    console.log('loading service');
  }
  loaded(){
    console.log('loaded service');
  }
}
