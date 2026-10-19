import { createApp, markRaw } from 'vue';
import { createPinia } from 'pinia';
import WorkshopView from './WorkshopView.vue';
import bundle from 'virtual:workshop-offline-bundle';
import '../../application/application.css';

// The packager validates hashes before bundling. No runtime fetch or cloud session.
createApp(WorkshopView, { bundle: markRaw(bundle), offline: true }).use(createPinia()).mount('#workshop-offline');
