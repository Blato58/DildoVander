import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './application/App.vue';
import { router } from './application/router';
import './application/application.css';

createApp(App).use(createPinia()).use(router).mount('#app');
