declare const __DV_APPLICATION_BUILD__: { revision: string | null; dirty: boolean | null };

// Embedded by the shared web/Capacitor build, never fetched from a mutable server
// release pointer. This identifies the assets actually running in this tab.
export const applicationBuild = typeof __DV_APPLICATION_BUILD__ === 'undefined'
  ? { revision: null, dirty: null }
  : __DV_APPLICATION_BUILD__;
