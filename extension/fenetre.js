const HOTE = 'fr.marioteck.prisme';
const $ = (id) => document.getElementById(id);
let lien = null, dossier = 'Téléchargements\\Prisme';

function natif(msg) {
  return new Promise((ok) => chrome.runtime.sendNativeMessage(HOTE, msg, (rep) =>
    ok(chrome.runtime.lastError || !rep ? { ok: false, absent: true } : rep)));
}

function retour(texte, genre) {
  const r = $('retour');
  r.hidden = false; r.className = 'retour ' + (genre || ''); r.textContent = texte;
  r.style.animation = 'none'; r.offsetHeight; r.style.animation = '';
}

const ABSENT = 'Prisme ne répond pas : installe Prisme 1.3 ou plus récent (github.com/mario07teck-sys/Prisme), puis rouvre ce navigateur.';

(async () => {
  const [onglet] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (onglet && /^https?:\/\//i.test(onglet.url || '')) {
    lien = onglet.url;
    $('titre').textContent = onglet.title || lien;
    $('site').textContent = new URL(lien).hostname.replace(/^www\./, '');
  } else {
    $('titre').textContent = 'Rien à télécharger ici';
    $('site').textContent = 'Ouvre la page d\'une vidéo (YouTube, X, TikTok…)';
    document.querySelectorAll('button').forEach((b) => (b.disabled = true));
  }
  const ping = await natif({ ping: true });
  $('etat').textContent = ping.ok ? `Prisme ${ping.version || ''} prêt` : 'Prisme absent';
  $('etat').className = 'etat ' + (ping.ok ? 'pret' : 'absent');
  if (ping.dir) dossier = ping.dir;
  if (!ping.ok) retour(ABSENT, 'err');
})();

document.querySelectorAll('button[data-mode]').forEach((b) => b.addEventListener('click', async () => {
  if (!lien) return;
  b.disabled = true;
  const rep = await natif({ url: lien, mode: b.dataset.mode });
  b.disabled = false;
  if (rep.ok) retour(`Envoyé à Prisme : elle arrive dans ${dossier}. Une notification préviendra à la fin.`, 'ok');
  else retour(rep.absent ? ABSENT : 'Prisme a refusé ce lien : ' + (rep.error || 'inconnu'), 'err');
}));
