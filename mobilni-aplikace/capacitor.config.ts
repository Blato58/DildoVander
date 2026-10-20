import type { CapacitorConfig } from '@capacitor/cli';

const config: CapacitorConfig = {
  appId: 'app.turquoise6409.green2444',
  appName: 'AI kufr',
  webDir: 'dist',
  // Native bridge calls include conversation text. Keep them out of device logs.
  loggingBehavior: 'none',
};

export default config;
