import { HttpErrorResponse } from '@angular/common/http';

export function mailRecycleError(error: HttpErrorResponse, fallback: string): string {
  const message = error.error?.message;
  if (typeof message === 'string' && message.trim()) return message;

  if (error.status === 404 || error.status === 405) {
    return 'Recycle is unavailable on this API, or the email has already been moved. Refresh and check that the NXPass API is up to date.';
  }
  if (error.status >= 500) {
    return 'The API could not complete the Recycle request. Ask your administrator to check the API logs and database migrations.';
  }
  return fallback;
}
