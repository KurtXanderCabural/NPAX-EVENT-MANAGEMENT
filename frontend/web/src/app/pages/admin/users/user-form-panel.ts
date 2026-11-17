import { Component, DestroyRef, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule, NgForm } from '@angular/forms';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../../core/api.service';
import { ToastService } from '../../../core/toast.service';
import { AdminUserUpsert, NotificationChannel, UserDto } from '../../../core/models';
import type { FaceCaptureResult } from '../../../shared/face-capture-dialog';
import { formatPhMobile } from '../../../shared/ph-phone';

// Shows the selected user for editing or a blank form for creating a user.
@Component({
  selector: 'app-user-form-panel',
  imports: [
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule,
    MatTooltipModule,
  ],
  templateUrl: './user-form-panel.html',
  styleUrl: './user-form-panel.scss',
})
export class UserFormPanelComponent {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private toast = inject(ToastService);
  private destroyRef = inject(DestroyRef);
  readonly loadingFaceDialog = signal(false);

  /** The record being edited; null = a new user. */
  readonly user = input<UserDto | null>(null);
  readonly saved = output<void>();

  private readonly form = viewChild(NgForm);

  firstName = '';
  middleInitial = '';
  lastName = '';
  senateNo = '';
  department = '';
  jobRole = '';
  email = '';
  username = '';
  password = '';
  role: string = 'Participant';
  phone = '';
  notificationChannel: NotificationChannel = 'Email';
  canConfirmRegistrations = false;

  // Photo state. existingPhoto = current photo (loaded on edit); newPhoto = a freshly picked
  // data URL; clearPhoto = the admin removed the existing one. On save: newPhoto wins, else
  // "" when cleared, else null (unchanged / none).
  existingPhoto = signal<string | null>(null);
  newPhoto = signal<string | null>(null);
  clearPhoto = signal(false);
  photoError = signal<string | null>(null);

  // Facial registration state. A newly captured descriptor (JSON), or "cleared".
  hadFace = false;
  newFace = signal<string | null>(null);
  clearFace = signal(false);

  saving = signal(false);

  // Bumped on every reset so a slow photo fetch for a previous row can't land on the current one.
  private loadSeq = 0;

  constructor() {
    effect(() => {
      const u = this.user();
      untracked(() => this.reset(u));
    });
  }

  get isEdit(): boolean {
    return !!this.user();
  }

  faceRegistered = () => !!this.newFace() || (this.hadFace && !this.clearFace());
  previewPhoto = () => this.newPhoto() ?? (this.clearPhoto() ? null : this.existingPhoto());

  // Viber passes are delivered to the phone number, so it's mandatory when Viber or Both is chosen.
  phoneRequired = () => this.notificationChannel === 'Viber' || this.notificationChannel === 'Both';

  /** True when the admin has changed anything since the record was loaded. */
  isDirty(): boolean {
    return !!this.form()?.dirty || !!this.newPhoto() || this.clearPhoto() || !!this.newFace() || this.clearFace();
  }

  private reset(u: UserDto | null): void {
    this.firstName = u?.firstName ?? '';
    this.middleInitial = u?.middleInitial ?? '';
    this.lastName = u?.lastName ?? '';
    this.senateNo = u?.senateNo ?? '';
    this.department = u?.department ?? '';
    this.jobRole = u?.jobRole ?? '';
    this.email = u?.email ?? '';
    this.username = u?.username ?? '';
    this.password = '';
    this.role = u?.role ?? 'Participant';
    this.phone = formatPhMobile(u?.phone);
    this.notificationChannel = u?.notificationChannel ?? 'Email';
    this.canConfirmRegistrations = u?.canConfirmRegistrations ?? false;
    this.hadFace = u?.hasFace ?? false;

    this.existingPhoto.set(null);
    this.newPhoto.set(null);
    this.clearPhoto.set(false);
    this.photoError.set(null);
    this.newFace.set(null);
    this.clearFace.set(false);

    // Clear dirty/touched flags once the new values have been bound.
    queueMicrotask(() => this.form()?.resetForm(this.formValues()));

    // The list omits the photo to stay light, so fetch the full record.
    const seq = ++this.loadSeq;
    if (u) {
      this.api.getUser(u.id).subscribe({
        next: (full) => {
          if (seq === this.loadSeq) this.existingPhoto.set(full.photoUrl);
        },
        error: () => {},
      });
    }
  }

  // resetForm() would blank ngModel-bound fields, so hand it the current values back.
  private formValues(): Record<string, unknown> {
    return {
      firstName: this.firstName,
      middleInitial: this.middleInitial,
      lastName: this.lastName,
      senateNo: this.senateNo,
      department: this.department,
      jobRole: this.jobRole,
      email: this.email,
      username: this.username,
      password: '',
      role: this.role,
      notificationChannel: this.notificationChannel,
      phone: this.phone,
      canConfirm: this.canConfirmRegistrations,
    };
  }

  // Live-format the phone field to the PH mobile mask "+63 000 000 0000".
  onPhoneInput(e: Event): void {
    const el = e.target as HTMLInputElement;
    this.phone = formatPhMobile(el.value);
    el.value = this.phone;
  }

  onPhotoSelected(event: Event): void {
    const el = event.target as HTMLInputElement;
    const file = el.files?.[0];
    el.value = '';
    if (!file) return;

    this.photoError.set(null);
    if (!file.type.startsWith('image/')) {
      this.photoError.set('Please choose an image file.');
      return;
    }
    if (file.size > 8 * 1024 * 1024) {
      this.photoError.set('Image is too large (max 8 MB before compression).');
      return;
    }

    const reader = new FileReader();
    const seq = this.loadSeq;
    reader.onload = () => {
      const img = new Image();
      img.onload = () => {
        if (seq !== this.loadSeq) return;
        const maxDim = 512;
        const scale = Math.min(1, maxDim / Math.max(img.width, img.height));
        const w = Math.round(img.width * scale);
        const h = Math.round(img.height * scale);
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        canvas.getContext('2d')!.drawImage(img, 0, 0, w, h);
        this.newPhoto.set(canvas.toDataURL('image/jpeg', 0.85));
        this.clearPhoto.set(false);
      };
      img.onerror = () => {
        if (seq === this.loadSeq) this.photoError.set('Could not read that image.');
      };
      img.src = reader.result as string;
    };
    reader.readAsDataURL(file);
  }

  removePhoto(): void {
    this.newPhoto.set(null);
    this.clearPhoto.set(true);
    this.photoError.set(null);
  }

  async registerFace(): Promise<void> {
    if (this.loadingFaceDialog()) return;
    this.loadingFaceDialog.set(true);
    try {
      const { FaceCaptureDialogComponent } = await import('../../../shared/face-capture-dialog');
      if (this.destroyRef.destroyed) return;
      this.dialog
        .open(FaceCaptureDialogComponent, { width: '420px', maxWidth: '94vw', autoFocus: false })
        .afterClosed()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe((res: FaceCaptureResult | undefined) => {
          if (!res) return;
          // The captured face doubles as the photo, since the Face TV recognizes people from it.
          this.newFace.set(JSON.stringify(res.descriptor));
          this.clearFace.set(false);
          this.newPhoto.set(res.image);
          this.clearPhoto.set(false);
        });
    } catch {
      this.toast.error('Could not open face registration. Try again.');
    } finally {
      this.loadingFaceDialog.set(false);
    }
  }

  removeFace(): void {
    this.newFace.set(null);
    this.clearFace.set(true);
  }

  private photoPayload(): string | null {
    if (this.newPhoto()) return this.newPhoto();
    if (this.clearPhoto()) return '';
    return null;
  }

  private facePayload(): string | null {
    if (this.newFace()) return this.newFace();
    if (this.clearFace()) return '';
    return null;
  }

  get valid(): boolean {
    const hasNames = !!this.firstName.trim() && !!this.lastName.trim();
    const departmentOk = this.isEdit || !!this.department.trim();
    const email = this.email.trim();
    const emailOk = !email || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
    const pwOk = !this.isEdit || !this.password.trim() || this.password.length >= 8;
    // A phone is optional unless Viber; when present it must be a complete PH mobile number.
    const phone = this.phone.trim();
    const phoneComplete = /^\+63 \d{3} \d{3} \d{4}$/.test(phone);
    const phoneOk = phone ? phoneComplete : !this.phoneRequired();
    return hasNames && departmentOk && emailOk && pwOk && !!this.role && phoneOk;
  }

  save(): void {
    if (this.saving()) return;
    if (!this.valid) {
      this.form()?.form.markAllAsTouched();
      this.toast.warning('Please complete the required fields.');
      return;
    }
    this.saving.set(true);

    const current = this.user();
    const body: AdminUserUpsert = {
      firstName: this.firstName.trim(),
      middleInitial: this.middleInitial.trim() || null,
      lastName: this.lastName.trim(),
      username: this.username.trim() || null,
      senateNo: this.senateNo.trim() || null,
      level: current?.level ?? null,
      department: this.department.trim(),
      jobRole: this.jobRole.trim(),
      email: this.email.trim(),
      password: this.isEdit && this.password.trim() ? this.password : null,
      role: this.role,
      organization: current?.organization ?? null,
      phone: this.phone.trim() || null,
      canConfirmRegistrations: this.canConfirmRegistrations,
      photo: this.photoPayload(),
      face: this.facePayload(),
      notificationChannel: this.notificationChannel,
    };

    const req = current ? this.api.updateUser(current.id, body) : this.api.createUser(body);

    req.subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success(current ? 'User updated.' : 'User added.');
        // A new draft already has a null user input, so deselecting won't reset it.
        if (!this.isEdit) this.reset(null);
        this.saved.emit();
      },
      error: (err) => {
        this.saving.set(false);
        this.toast.error(err?.error?.message ?? 'Could not save the user.');
      },
    });
  }
}
