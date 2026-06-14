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
databases: any[] = [];

ngOnInit(): void {
  this.form=this.initForm();
  this.loadDatabases();
}
users =inject(AuthService);
fb =inject(FormBuilder);

loadDatabases() {
  this.auth.getDatabases().subscribe({
    next: (res: any) => {
      this.databases = res;
      if (res && res.length > 0) {
        const currentDb = localStorage.getItem('db-selection');
        const dbExists = res.some(db => db.id === currentDb);
        const has2027 = res.some(db => db.id === '2027');
        const defaultDb = dbExists ? currentDb : (has2027 ? '2027' : res[0].id);
        this.form.get('database')?.setValue(defaultDb);
        if (!dbExists) {
          localStorage.setItem('db-selection', defaultDb);
        }
      }
    },
    error: (err) => console.log('Error loading databases', err)
  });
}

initForm(){
  return this.fb.group({
    username:['bob'],
    password:['Pass123$'],
    database:['']
  })
}
onSubmit(){
  const db = this.form.value.database;
  if (db) {
    localStorage.setItem('db-selection', db);
  }
  this. auth.login(this.form.value)
}
logout(){
  this.auth.logout();
}


}



