import { Directive, ElementRef, HostListener, Input, OnDestroy, OnInit } from '@angular/core';

@Directive({
  selector: '[appDragScroll]',
  standalone: false
})
export class DragScrollDirective implements OnInit, OnDestroy {
  @Input() dragScrollDisabled = false;
  @Input() dragThreshold = 5;

  private isDragging = false;
  private startX = 0;
  private startY = 0;
  private lastClientX = 0;
  private lastClientY = 0;
  private hasMoved = false;

  private clickCaptureListener?: (e: MouseEvent) => void;

  constructor(private el: ElementRef<HTMLElement>) {}

  ngOnInit(): void {
    const nativeElement = this.el.nativeElement;
    nativeElement.classList.add('app-drag-scroll');

    // Capture-phase click listener to suppress accidental click triggers when releasing a drag gesture
    this.clickCaptureListener = (e: MouseEvent) => {
      if (this.hasMoved) {
        e.preventDefault();
        e.stopPropagation();
        this.hasMoved = false;
      }
    };
    nativeElement.addEventListener('click', this.clickCaptureListener, true);
  }

  ngOnDestroy(): void {
    if (this.clickCaptureListener) {
      this.el.nativeElement.removeEventListener('click', this.clickCaptureListener, true);
    }
  }

  @HostListener('mousedown', ['$event'])
  onMouseDown(event: MouseEvent): void {
    if (this.dragScrollDisabled || event.button !== 0) {
      return; // Only allow left-click drag when not disabled
    }

    const target = event.target as HTMLElement;
    // Exclude interactive elements so users can interact naturally with inputs, buttons, checkboxes, etc.
    if (
      target.closest(
        'input, textarea, select, button, a, mat-checkbox, mat-button-toggle, mat-icon-button, [role="button"], .mat-mdc-menu-trigger, .no-drag, .mat-mdc-paginator'
      )
    ) {
      return;
    }

    this.isDragging = true;
    this.hasMoved = false;
    this.startX = event.clientX;
    this.startY = event.clientY;
    this.lastClientX = event.clientX;
    this.lastClientY = event.clientY;

    const el = this.el.nativeElement;
    el.style.cursor = 'grabbing';
    el.style.userSelect = 'none';
  }

  @HostListener('window:mousemove', ['$event'])
  onMouseMove(event: MouseEvent): void {
    if (!this.isDragging) return;

    const deltaX = event.clientX - this.lastClientX;
    const deltaY = event.clientY - this.lastClientY;

    const totalMoved = Math.hypot(event.clientX - this.startX, event.clientY - this.startY);
    if (totalMoved > this.dragThreshold) {
      this.hasMoved = true;
    }

    const el = this.el.nativeElement;
    el.scrollLeft -= deltaX;
    el.scrollTop -= deltaY;

    this.lastClientX = event.clientX;
    this.lastClientY = event.clientY;
  }

  @HostListener('window:mouseup', ['$event'])
  onMouseUp(): void {
    if (!this.isDragging) return;

    this.isDragging = false;
    const el = this.el.nativeElement;
    el.style.cursor = '';
    el.style.userSelect = '';

    // Allow slight delay before resetting hasMoved so clickCaptureListener can catch the mouse release
    if (this.hasMoved) {
      setTimeout(() => {
        this.hasMoved = false;
      }, 50);
    }
  }
}
