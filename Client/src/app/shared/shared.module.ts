import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AngularComponentsModule } from './angular-components.module';
import {  ReactiveFormsModule } from '@angular/forms';
import { InputTextComponent } from './components/input-text/input-text.component';
import { GalleryModule } from 'ng-gallery';



@NgModule({
  imports: [

    InputTextComponent,
  ]
  ,exports:[
    CommonModule,
    AngularComponentsModule,
    ReactiveFormsModule,
    InputTextComponent,
    GalleryModule

  ]
})
export class SharedModule { }
