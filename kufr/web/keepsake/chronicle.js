import { button, escapeHtml, notice, statusBadge } from '/design-system/ui.js';

const element = (id) => document.getElementById(id);
element('chronicle-actions').innerHTML = button({ id: 'refresh-chronicle', label: 'Obnovit kroniku', variant: 'secondary' });
element('admin-actions').innerHTML = button({ label: 'Načíst soukromý přehled', type: 'submit' });
let loading = false;

function render(snapshot) {
  if (!snapshot || snapshot.schemaVersion !== 1 || !Array.isArray(snapshot.wands)) throw new Error('invalid_snapshot');
  element('chronicle').hidden = false;
  element('chronicle-edition').innerHTML = statusBadge({ label: snapshot.provisional ? 'Živý provizorní přehled' : `Publikovaná edice ${snapshot.revision}`, tone: snapshot.provisional ? 'info' : 'success' })
    + `<span class="chronicle-note">${escapeHtml(new Date(snapshot.createdAt).toLocaleString('cs-CZ'))}</span>`;
  element('completed-visits').textContent = String(snapshot.completedVisits);
  element('confirmed-presentations').textContent = String(snapshot.confirmedPresentations);
  element('completed-duels').textContent = String(snapshot.completedDuels);
  element('narrative').textContent = snapshot.narrative;
  element('chronicle-wands').innerHTML = snapshot.wands.map((wand) => `<tr><th scope="row">${escapeHtml(wand.officialName)}</th><td>${escapeHtml(wand.confirmedPresentations)}</td></tr>`).join('');
  const popular = snapshot.wands.filter((wand) => snapshot.mostPopularSlots.includes(wand.slot));
  element('popular-wands').textContent = popular.length === 0 ? 'První potvrzená prezentace teprve přijde.'
    : `${popular.length > 1 ? 'Na prvním místě je remíza' : 'Nejvíc potvrzených prezentací'}: ${popular.map((wand) => wand.officialName).join(', ')}.`;
  element('unresolved').textContent = `Neuzavřené návštěvy: ${snapshot.unresolvedVisits}. Přerušené návštěvy: ${snapshot.interruptedVisits}. Případné pozdní potvrzení patří původnímu večeru a vytvoří další edici.`;
}

async function loadPublic() {
  if (loading) return;
  loading = true;
  element('refresh-chronicle').disabled = true;
  element('chronicle-status').innerHTML = notice({ message: 'Načítám večerní knihu…' });
  // Hide previously rendered data immediately; a revoked source must not remain
  // visible in an old panel while an unavailable refresh is being explained.
  element('chronicle').hidden = true;
  element('admin-detail').textContent = '';
  try {
    const response = await fetch('/guest/v1/chronicle', { cache: 'no-store', credentials: 'omit', redirect: 'error' });
    if (response.status === 404) {
      element('chronicle-status').innerHTML = notice({ message: 'Kronika zatím není dostupná. Obsluha musí nejprve připravit večer; později ji můžeš obnovit.' });
      return;
    }
    if (!response.ok) throw new Error('unavailable');
    render(await response.json());
    element('chronicle-status').replaceChildren();
  } catch {
    element('chronicle-status').innerHTML = notice({ tone: 'warning', message: 'Večerní kniha teď není dostupná. Zkus ji znovu obnovit.' });
  } finally {
    loading = false;
    element('refresh-chronicle').disabled = false;
  }
}

element('refresh-chronicle').addEventListener('click', loadPublic);
element('admin-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  if (loading) return;
  const form = event.currentTarget;
  const values = new FormData(form);
  const credential = String(values.get('credential'));
  form.elements.credential.value = '';
  loading = true;
  element('chronicle').hidden = true;
  element('admin-detail').textContent = 'Načítám soukromý přehled…';
  try {
    const query = new URLSearchParams({ deviceId: String(values.get('device')), eveningId: String(values.get('evening')) });
    const response = await fetch(`/v1/admin/show/chronicle?${query}`, { cache: 'no-store', credentials: 'omit', redirect: 'error', headers: { Authorization: `Bearer ${credential}` } });
    if (!response.ok) throw new Error('unavailable');
    const result = await response.json();
    render(result.snapshot);
    element('chronicle-status').innerHTML = statusBadge({ label: 'Soukromý přehled obsluhy', tone: 'warning' });
    element('admin-detail').textContent = `Archivované edice: ${result.archivedEditions}. Vyčištění záloh není potvrzené. Vyčištění u poskytovatele nebylo vyžádáno.`;
  } catch {
    element('admin-detail').textContent = 'Přehled není dostupný. Zkontroluj oprávnění a ID zařízení i večera.';
  } finally {
    loading = false;
  }
});

loadPublic();
