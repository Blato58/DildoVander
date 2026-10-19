import '../../../../mechanika/final/twin_media_core.js';
import * as controllerModule from '../../../../mechanika/final/twin_media_controller.js';
const runtime = typeof window === 'undefined' ? globalThis : window;
/** @type {any} Shared headless browser HAL; no DOM application is mounted. */
export const mediaController = controllerModule.default || runtime.TwinMediaController;
