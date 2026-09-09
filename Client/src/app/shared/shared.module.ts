import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AngularComponentsModule } from './angular-components.module';
import {  ReactiveFormsModule } from '@angular/forms';
import { InputTextComponent } from './components/input-text/input-text.component';
import { GalleryModule } from 'ng-gallery';



import { DragScrollDirective } from './directives/drag-scroll.directive';

@NgModule({
  declarations: [
    DragScrollDirective
  ],
  imports: [
    InputTextComponent,
  ],
  exports: [
    CommonModule,
    AngularComponentsModule,
    ReactiveFormsModule,
    InputTextComponent,
    GalleryModule,
    DragScrollDirective
  ]
})
export class SharedModule { }
