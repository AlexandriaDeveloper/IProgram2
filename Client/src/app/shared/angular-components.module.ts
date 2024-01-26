
import { NgModule } from '@angular/core';
import { MatButtonModule} from '@angular/material/button';
import { MatSidenavModule} from '@angular/material/sidenav';
import { MatFormFieldModule} from '@angular/material/form-field';
import {MatCardModule}   from '@angular/material/card';
import {MatIconModule} from '@angular/material/icon';
import {MatInputModule} from '@angular/material/input';
import {MatSelectModule} from '@angular/material/select';
import {MatExpansionModule} from '@angular/material/expansion';
import {MatProgressBarModule} from '@angular/material/progress-bar';
import {MatTooltipModule} from '@angular/material/tooltip';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatTableModule } from '@angular/material/table';
import { MatSortModule } from '@angular/material/sort';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatNativeDateModule } from '@angular/material/core';
import { MatMenuModule } from '@angular/material/menu';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import {DragDropModule} from '@angular/cdk/drag-drop';
import { MatMomentDateModule } from "@angular/material-moment-adapter";
import {MatCheckboxModule} from '@angular/material/checkbox';
import {MatTabsModule} from '@angular/material/tabs';
import {MatListModule} from '@angular/material/list';
import {MatBottomSheetModule} from '@angular/material/bottom-sheet';
@NgModule({
  declarations: [],
  imports: [
    MatIconModule,
    MatInputModule,
    MatDatepickerModule,
    MatMomentDateModule,
    MatNativeDateModule
  ],
  providers: [
    MatDatepickerModule,
     MatMomentDateModule,
     MatNativeDateModule
  ],
  exports:[
    MatButtonModule,
    MatSidenavModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatCardModule,
    MatTooltipModule,
    MatSelectModule,
    MatExpansionModule,
    MatProgressBarModule,
    MatPaginatorModule,
    MatTableModule,
    MatSortModule,
    MatDatepickerModule,
    MatNativeDateModule,
    MatMenuModule,
    MatAutocompleteModule,
    DragDropModule,
    MatMomentDateModule,
    MatCheckboxModule,
    MatTabsModule,MatListModule,
    MatBottomSheetModule,


  ]
})
export class AngularComponentsModule {

 }
