import { Component, OnInit, inject } from '@angular/core';
import { SharedModule } from '../../shared/shared.module';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { AuthService } from '../../shared/service/auth.service';
import { ToasterService } from '../../shared/components/toaster/toaster.service';
import { IRole } from '../../shared/models/roles';
import { IRegisterRequest } from '../../shared/models/registerRequest';
import { RoleService } from '../../shared/service/role.service';

@Component({
  selector: 'app-register',
  standalone: false,
  //imports: [SharedModule],
  templateUrl: './register.component.html',
  styleUrl: './register.component.scss'
})
export class RegisterComponent implements OnInit {
  form:FormGroup;
  auth =inject(AuthService);
  roleService =inject(RoleService);
  toast =inject(ToasterService);
  users =inject(AuthService);
  fb =inject(FormBuilder);

  roles :IRole[] = [];
  request : IRegisterRequest ={
    username:'seagaull',
    displayName:'محمد على شريف',
    email:'seaagull@hotmail.com',
    password:'Pass123$',
    roles:null
  }


  ngOnInit(): void {
    this.form=this.initForm();

    this.roleService.getRoles().subscribe({
      next:(res:any)=>{
        this.roles=res;
      },
      error:(err)=> console.log(err)
    });
  }


initForm(){
  return this.fb.group({
    username:[this.request.username,[Validators.required,Validators.minLength(3),Validators.maxLength(20)]],
    displayName:[this.request.displayName,[Validators.required,Validators.minLength(3),Validators.maxLength(20)]],
    email:[this.request.email,[Validators.required,Validators.email]],
    password:[this.request.password,Validators.required],
    roles:[this.request.roles,Validators.required],

  })
}
  onSubmit(){


    this.auth.signup(this.form.value).subscribe({
      next:(res:any)=>{
        // console.log(res)
        this.form.reset();
        this.toast.openSuccessToaster('تم التسجيل بنجاح','check');
      },
      error:(err)=> console.log(err)
    })

  }
}
