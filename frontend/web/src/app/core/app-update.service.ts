import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SwUpdate } from '@angular/service-worker';

@Injectable({ providedIn: 'root' })
export class AppUpdateService {
  private updates = inject(SwUpdate);
  private destroyRef = inject(DestroyRef);
  readonly available = signal(false);

  constructor() {
    if (!this.updates.isEnabled) return;
    this.updates.versionUpdates.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.type === 'VERSION_READY') this.available.set(true);
    });
    this.updates.unrecoverable.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.available.set(true));
  }

  reload(): void {
    // Reload the complete application instead of activating a worker mid-session,
    // which could mix an old main bundle with newly hashed lazy module chunks.
    globalThis.location.reload();
  }
}
