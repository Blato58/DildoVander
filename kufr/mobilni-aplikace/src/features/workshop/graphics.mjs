import * as graphicsModule from '../../../../mechanika/final/viewer_engine_core.js';
const runtime = typeof window === 'undefined' ? globalThis : window;
/** Original F2 graphics algorithms, shared with the compatibility viewer. */
export const graphics = graphicsModule.default || runtime.F2GraphicsCore;
