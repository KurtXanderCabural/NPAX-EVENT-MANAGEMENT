import { environment } from '../../environments/environment';
import { resolveApiBase } from './api-base.util';

// Values are set per build in src/environments/.
export const API_BASE = resolveApiBase(environment.apiBaseUrl, globalThis.location?.hostname ?? '');
export const FACE_ENGINE_BASE = environment.faceEngineBaseUrl;
