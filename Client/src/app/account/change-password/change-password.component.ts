import { AuthService } from './../../shared/service/auth.service';
import { AfterViewInit, Component, OnInit, inject } from '@angular/core';
import { SharedModule } from "../../shared/shared.module";
import { InputTextComponent } from "../../shared/components/input-text/input-text.component";
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ToasterService } from '../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-change-password',
  standalone: false,

  templateUrl: './change-password.component.html',
  styleUrl: './change-password.component.scss'
})
export class ChangePasswordComponent implements OnInit, AfterViewInit {
  auth =inject(AuthService);
  toaster = inject(ToasterService);
  changePasswordForm :FormGroup;
  constructor(private formBuilder: FormBuilder) {}
  ngAfterViewInit(): void {

  }
  ngOnInit(): void {
   this.changePasswordForm= this.initForm();
  }
  initForm(){
    return this.formBuilder.group({
      oldPassword: ['', Validators.required],
      newPassword: ['', Validators.required],
      confirmPassword: ['', Validators.required],
    });
  }
  changePassword(){
    var  model = this.changePasswordForm.value;
console.log(model);

    this.auth.changePassword(model).subscribe(x => {
    this.toaster.openSuccessToaster("تم تغيير كلمة المرور بنجاح","check")
    this.changePasswordForm.reset();
    });
}
}
