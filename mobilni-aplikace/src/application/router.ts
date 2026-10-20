import { createRouter, createWebHistory } from 'vue-router';
import { installAccessGuard } from './accessGuard';

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', name: 'home', component: () => import('./HomeView.vue'), meta: { title: 'Domov' } },
    { path: '/persona-lab', name: 'persona-lab', component: () => import('../features/persona-lab/PersonaLabView.vue'), meta: { title: 'AI Persona Lab', private: true, requiresAccount: true } },
    { path: '/suitcase', name: 'suitcase', component: () => import('../features/suitcase/SuitcaseView.vue'), meta: { title: 'Kufr', keepAlive: true, requiresAccount: true, operation: true, offlineLocal: true, private: true } },
    { path: '/connections', name: 'connections', component: () => import('../features/suitcase/ConnectionsView.vue'), meta: { title: 'Připojení', keepAlive: true, requiresAccount: true, operation: true, offlineLocal: true, private: true } },
    { path: '/suitcase/diagnostics', name: 'diagnostics', component: () => import('../features/suitcase/DiagnosticsView.vue'), meta: { title: 'Diagnostika', keepAlive: true, requiresAccount: true, operation: true, offlineLocal: true, private: true } },
    { path: '/suitcase/director', name: 'director', component: () => import('../features/director/DirectorPanel.vue'), meta: { title: 'Režie', private: true, requiresAccount: true, operation: true } },
    { path: '/workshop', name: 'workshop', component: () => import('../features/workshop/WorkshopView.vue'), meta: { title: 'Dílna' } },
    { path: '/archive', name: 'archive', component: () => import('../features/archive/ArchiveView.vue'), meta: { title: 'Archiv' } },
    { path: '/archive/duel', name: 'duel', component: () => import('../features/archive/DuelView.vue'), meta: { title: 'Souboj', private: true } },
    { path: '/archive/chronicle', name: 'chronicle', component: () => import('../features/archive/ChronicleView.vue'), meta: { title: 'Kronika' } },
    { path: '/keepsake/duel.html', redirect: to => ({ path: '/archive/duel', query: to.query }) },
    { path: '/keepsake/chronicle.html', redirect: to => ({ path: '/archive/chronicle', query: to.query }) },
    { path: '/keepsake/:pathMatch(.*)*', name: 'keepsake', component: () => import('../features/archive/KeepsakeView.vue'), meta: { title: 'Moje památka', private: true } },
    { path: '/signin', name: 'signin', component: () => import('../features/identity/SignInView.vue'), meta: { title: 'Přihlášení', private: true } },
    { path: '/settings', name: 'settings', component: () => import('../features/identity/SettingsView.vue'), meta: { title: 'Nastavení', private: true, requiresAccount: true } },
    { path: '/settings/admin', name: 'admin', component: () => import('../features/identity/AdminView.vue'), meta: { title: 'Správa', private: true, requiresAccount: true, administrator: true } },
    { path: '/app/:pathMatch(.*)*', redirect: '/suitcase' },
    { path: '/companion/:pathMatch(.*)*', redirect: '/suitcase' },
    { path: '/simulator/:pathMatch(.*)*', redirect: { path: '/workshop', query: { mode: 'simulation' } } },
    { path: '/workshop/index.html', redirect: '/workshop' },
    { path: '/workshop/viewer.html', redirect: '/workshop' },
    { path: '/:pathMatch(.*)*', component: () => import('./NotFoundView.vue'), meta: { title: 'Stránka nenalezena' } },
  ],
  scrollBehavior(to, from, saved) { return saved ?? (to.path === from.path ? false : { top: 0 }); },
});

router.beforeEach(to => {
  if (to.hash === '#director' && ['/', '/suitcase', '/suitcase/diagnostics'].includes(to.path)) return {path: '/suitcase/director'};
  if (['/keepsake/evening/', '/keepsake/evening', '/keepsake/evening/index.html'].includes(to.path)) {
    return { path: '/archive', query: to.query };
  }
});
router.afterEach(to => {
  document.title = `${to.meta.title ?? 'DildoVander'} · DildoVander`;
  let robots = document.querySelector<HTMLMetaElement>('meta[name="robots"]');
  if (to.meta.private) {
    if (!robots) { robots = document.createElement('meta'); robots.name = 'robots'; document.head.append(robots); }
    robots.content = 'noindex, nofollow, noarchive';
  } else { robots?.remove(); }
});

installAccessGuard(router);
