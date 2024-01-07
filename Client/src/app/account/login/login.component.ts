import { HttpClient, HttpClientModule } from '@angular/common/http';
import { Component, OnInit, inject } from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { AuthService } from '../../shared/service/auth.service';
import { SharedModule } from '../../shared/shared.module';
import { ToasterService } from '../../shared/components/toaster/toaster.service';

@Component({
  selector: 'app-login',
 standalone: false,
 // imports: [SharedModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss'
})
export class LoginComponent implements OnInit {
form:FormGroup;
auth =inject(AuthService);

ngOnInit(): void {
  this.form=this.initForm();
}
users =inject(AuthService);
fb =inject(FormBuilder);

initForm(){
  return this.fb.group({
    username:['bob'],
    password:['Pass123$']
  })
}
onSubmit(){
 this. auth.login(this.form.value)
}
logout(){
  this.auth.logout();
}


}



