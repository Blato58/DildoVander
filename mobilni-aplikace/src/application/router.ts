import { createRouter, createWebHistory, type RouteLocationNormalizedLoaded } from 'vue-router';
import { watch } from 'vue';
import { installAccessGuard } from './accessGuard';
import { customerCopy, type CustomerCopyKey } from './customerCopy';

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', name: 'home', component: () => import('./HomeView.vue'), meta: { title: 'Domov' } },
    { path: '/demo', name: 'customer-demo', component: () => import('../features/demo/CustomerDemo.vue'), meta: { title: 'Ukázkový příběh' } },
    { path: '/support', name: 'support', component: () => import('../features/information/SupportView.vue'), meta: { title: 'Podpora Wand Night Stand' } },
    { path: '/privacy', name: 'privacy', component: () => import('../features/information/PrivacyView.vue'), meta: { title: 'Ochrana soukromí' } },
    { path: '/account-deletion', name: 'account-deletion', component: () => import('../features/identity/AccountDeletionStatusView.vue'), meta: { title: 'Stav smazání účtu', private: true } },
    { path: '/me', redirect: '/me/character' },
    { path: '/me/character', name: 'customer-character', component: () => import('../features/customer/CustomerView.vue'), meta: { title: 'Moje postava', private: true, requiresAccount: true } },
    { path: '/me/conversation', name: 'customer-conversation', component: () => import('../features/customer/CustomerView.vue'), meta: { title: 'Rozhovor s kufrem', private: true, requiresAccount: true } },
    { path: '/me/keepsakes', name: 'customer-keepsakes', component: () => import('../features/customer/CustomerView.vue'), meta: { title: 'Moje památky', private: true, requiresAccount: true } },
    { path: '/me/account', name: 'customer-account', component: () => import('../features/customer/CustomerView.vue'), meta: { title: 'Můj účet', private: true, requiresAccount: true } },
    { path: '/persona-lab', name: 'persona-lab', component: () => import('../features/persona-lab/PersonaLabView.vue'), meta: { title: 'AI Persona Lab', private: true, requiresAccount: true, staff: true } },
    { path: '/tonight', name: 'tonight', component: () => import('../features/tonight/TonightPage.vue'), meta: { title: 'Dnešní provoz', private: true, requiresAccount: true, operation: true, staff: true } },
    { path: '/suitcase', name: 'suitcase', component: () => import('../features/suitcase/SuitcaseView.vue'), meta: { title: 'Kufr', keepAlive: true, requiresAccount: true, operation: true, offlineLocal: true, private: true, staff: true } },
    { path: '/connections', name: 'connections', component: () => import('../features/suitcase/ConnectionsView.vue'), meta: { title: 'Připojení', keepAlive: true, requiresAccount: true, operation: true, offlineLocal: true, private: true, staff: true } },
    { path: '/suitcase/diagnostics', name: 'diagnostics', component: () => import('../features/suitcase/DiagnosticsView.vue'), meta: { title: 'Diagnostika', keepAlive: true, requiresAccount: true, operation: true, offlineLocal: true, private: true, staff: true } },
    { path: '/suitcase/director', name: 'director', component: () => import('../features/director/DirectorPanel.vue'), meta: { title: 'Režie', private: true, requiresAccount: true, operation: true, staff: true } },
    { path: '/workshop', name: 'workshop', component: () => import('../features/workshop/WorkshopView.vue'), meta: { title: 'Dílna' } },
    { path: '/archive', name: 'archive', component: () => import('../features/archive/ArchiveView.vue'), meta: { title: 'Archiv' } },
    { path: '/archive/duel', name: 'duel', component: () => import('../features/archive/DuelView.vue'), meta: { title: 'Souboj', private: true } },
    { path: '/archive/chronicle', name: 'chronicle', component: () => import('../features/archive/ChronicleView.vue'), meta: { title: 'Kronika' } },
    { path: '/keepsake/duel.html', redirect: to => ({ path: '/archive/duel', query: to.query }) },
    { path: '/keepsake/chronicle.html', redirect: to => ({ path: '/archive/chronicle', query: to.query }) },
    { path: '/keepsake/:pathMatch(.*)*', name: 'keepsake', component: () => import('../features/archive/KeepsakeView.vue'), meta: { title: 'Moje památka', private: true } },
    { path: '/signin', name: 'signin', component: () => import('../features/identity/SignInView.vue'), meta: { title: 'Přihlášení', private: true } },
    { path: '/settings', name: 'settings', component: () => import('../features/identity/SettingsView.vue'), meta: { title: 'Nastavení', private: true, requiresAccount: true, staff: true } },
    { path: '/settings/admin', name: 'admin', component: () => import('../features/identity/AdminView.vue'), meta: { title: 'Správa', private: true, requiresAccount: true, administrator: true, staff: true } },
    { path: '/admin/customer-sessions', name: 'customer-admin', component: () => import('../features/customer/CustomerAdminView.vue'), meta: { title: 'Zákaznické relace', private: true, requiresAccount: true, staff: true, operation: true } },
    { path: '/app/:pathMatch(.*)*', redirect: '/suitcase' },
    { path: '/companion/:pathMatch(.*)*', redirect: '/suitcase' },
    { path: '/simulator/:pathMatch(.*)*', redirect: { path: '/workshop', query: { mode: 'simulation' } } },
    { path: '/workshop/index.html', redirect: '/workshop' },
    { path: '/workshop/viewer.html', redirect: '/workshop' },
    { path: '/:pathMatch(.*)*', component: () => import('./NotFoundView.vue'), meta: { title: 'Stránka nenalezena' } },
  ],
  scrollBehavior(to, from, saved) { return saved ?? (to.path === from.path ? false : { top: 0 }); },
});

const localizedRouteTitles: Partial<Record<string, CustomerCopyKey>> = {
  home: 'home.title',
  'customer-demo': 'route.title-demo',
  support: 'support.title',
  privacy: 'privacy.title',
  'account-deletion': 'deletion.title',
  'customer-character': 'customer.character-pane',
  'customer-conversation': 'customer.conversation-pane',
  'customer-keepsakes': 'customer.keepsakes-pane',
  'customer-account': 'customer.account-pane',
  archive: 'route.title-archive',
  chronicle: 'route.title-chronicle',
  keepsake: 'route.title-keepsake',
  signin: 'signin.title',
  workshop: 'route.title-workshop',
  notFound: 'route.title-not-found',
};

function updateDocumentTitle(to: Pick<RouteLocationNormalizedLoaded, 'name' | 'meta'> = router.currentRoute.value): void {
  const key = to.name ? localizedRouteTitles[String(to.name)] : undefined;
  const title = key ? customerCopy.text(key) : to.meta.title ?? 'DildoVander';
  document.title = `${title} · DildoVander`;
}

router.beforeEach(to => {
  if (to.hash === '#director' && ['/', '/suitcase', '/suitcase/diagnostics'].includes(to.path)) return {path: '/suitcase/director'};
  if (['/keepsake/evening/', '/keepsake/evening', '/keepsake/evening/index.html'].includes(to.path)) {
    return { path: '/archive', query: to.query };
  }
});
router.afterEach(to => {
  updateDocumentTitle(to);
  let robots = document.querySelector<HTMLMetaElement>('meta[name="robots"]');
  if (to.meta.private) {
    if (!robots) { robots = document.createElement('meta'); robots.name = 'robots'; document.head.append(robots); }
    robots.content = 'noindex, nofollow, noarchive';
  } else { robots?.remove(); }
});

watch(customerCopy.language, () => updateDocumentTitle(), { flush: 'sync' });

installAccessGuard(router);
