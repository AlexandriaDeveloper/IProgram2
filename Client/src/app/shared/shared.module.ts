import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AngularComponentsModule } from './angular-components.module';
import {  ReactiveFormsModule } from '@angular/forms';
import { InputTextComponent } from './components/input-text/input-text.component';


@NgModule({
  imports: [

    InputTextComponent,
  ]
  ,exports:[
    CommonModule,
    AngularComponentsModule,
    ReactiveFormsModule,
    InputTextComponent,
  ]
})
export class SharedModule { }
