import { CommonModule } from '@angular/common';
import { Component, ElementRef, Input, OnInit, Self, ViewChild } from '@angular/core';
import { ControlValueAccessor, NgControl, ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import {MatProgressSpinnerModule} from '@angular/material/progress-spinner';
@Component({
  selector: 'app-input-text',
  standalone: true,
  imports: [
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    CommonModule
  ],
  templateUrl: './input-text.component.html',
  styleUrl: './input-text.component.scss'
})
export class InputTextComponent implements OnInit, ControlValueAccessor {
  ngOnInit(): void {
    const control = this.controlDir.control;
    const validators = control.validator ? [control.validator] : [];
    const asyncValidators = control.asyncValidator
      ? [control.asyncValidator]
      : [];

    control.setValidators(validators);
    control.setAsyncValidators(asyncValidators);
    control.updateValueAndValidity();
  }
  /**
   *
   */
  constructor(@Self() public controlDir: NgControl) {
    this.controlDir.valueAccessor = this;

  }
  @ViewChild('input', { static: true }) input: any;
   @Input() label: string;
   @Input() type: string = 'text';
  // @Input() name: string;
  // @Input() placeholder: string;
  // @Input() required: boolean;
   @Input() appearance: string="outline";
   @Input() matSuffix: string="settings";
  // @Input() matPrefix: string;
  // @Input() matHint: string;
  // @Input() matError: string;
  // @Input() matIcon: string;

  onChange(event) {}
  onTouched() {}
  writeValue(obj: any): void {
    console.log(obj)
    this.input.nativeElement.value = obj || '';
  }
  registerOnChange(fn: any): void {
    this.onChange = fn;
  }
  registerOnTouched(fn: any): void {
    this.onTouched = fn;
  }
  getErrorMessage() {
    let message = 'خطأ';

   if(this.controlDir.control.hasError('required')) {
     message = 'هذا الحقل مطلوب';
   }
   if(this.controlDir.control.hasError('email')) {
    message = 'البريد الالكتروني غير صحيح';
  }
  if (this.controlDir.control.hasError('pattern')) {
    message = 'الرقم القومي غير صحيح';
  }
  if(this.controlDir.control.hasError('minlength')) {
    message = 'الحد الادنى للحروف هو 8';
  }
  if(this.controlDir.control.hasError('maxlength')) {
    message = 'الحد الاقصى للحروف هو 10';
  }
  if(this.controlDir.control.hasError('min')) {
    message = 'الحد الادنى هو 0';
  }
  if(this.controlDir.control.hasError('max')) {
    message = 'الحد الاقصى هو 100';
  }
  if(this.controlDir.control.hasError('matDatepickerParse')) {
    message = 'التاريخ غير صحيح';
  }

   return message;
  }

}
