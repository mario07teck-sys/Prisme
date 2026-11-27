// Menu clic droit « Télécharger avec Prisme » : le lien part vers Prisme (native messaging), qui télécharge avec yt-dlp
// dans son propre dossier (Téléchargements\Prisme par défaut). Les téléchargements du navigateur ne sont pas touchés.
const HOTE = 'fr.marioteck.prisme';
const MODES = [
  ['best', 'Vidéo · meilleure qualité'],
  ['720', 'Vidéo · 720p (léger)'],
  ['mp3', 'Son seul · MP3'],
];
const CONTEXTES = ['video', 'audio', 'link', 'page', 'frame', 'image'];

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'prisme', title: 'Télécharger avec Prisme', contexts: CONTEXTES });
    for (const [mode, titre] of MODES)
      chrome.contextMenus.create({ id: 'prisme:' + mode, parentId: 'prisme', title: titre, contexts: CONTEXTES });
  });
});

// Le lien le plus utile à yt-dlp : un fichier vidéo direct s'il y en a un, sinon le lien survolé, sinon la page
// (YouTube, X, TikTok… lisent leurs vidéos par « blob: », inutilisable hors de la page : la page, elle, marche).
const DIRECT = /\.(mp4|webm|mkv|mov|m4v|mp3|m4a|ogg|opus|wav|flac|m3u8|mpd)(\?|#|$)/i;
function lienDe(info, onglet) {
  const http = (u) => typeof u === 'string' && /^https?:\/\//i.test(u);
  if (http(info.srcUrl) && (info.mediaType === 'video' || info.mediaType === 'audio') && DIRECT.test(info.srcUrl)) return info.srcUrl;
  if (http(info.linkUrl)) return info.linkUrl;
  if (http(info.frameUrl) && info.frameUrl !== info.pageUrl) return info.frameUrl; // vidéo intégrée (YouTube dans un article)
  if (http(info.pageUrl)) return info.pageUrl;
  return onglet && http(onglet.url) ? onglet.url : null;
}

function envoyer(url, mode) {
  return new Promise((ok) => {
    chrome.runtime.sendNativeMessage(HOTE, { url, mode }, (rep) => {
      if (chrome.runtime.lastError || !rep) ok({ ok: false, error: 'prisme-absent' });
      else ok(rep);
    });
  });
}

async function signaler(ongletId, reussi) {
  const opts = ongletId != null ? { tabId: ongletId } : {};
  await chrome.action.setBadgeBackgroundColor({ ...opts, color: reussi ? '#3DDC97' : '#FF5C7A' });
  await chrome.action.setBadgeText({ ...opts, text: reussi ? '✓' : '!' });
  setTimeout(() => chrome.action.setBadgeText({ ...opts, text: '' }), 4000);
}

chrome.contextMenus.onClicked.addListener(async (info, onglet) => {
  const mode = String(info.menuItemId).split(':')[1] || 'best';
  const url = lienDe(info, onglet);
  if (!url) return signaler(onglet?.id, false);
  const rep = await envoyer(url, mode);
  signaler(onglet?.id, rep.ok);
  if (!rep.ok) chrome.action.openPopup?.().catch(() => {}); // la petite fenêtre dit pourquoi (Prisme absent ou trop ancien)
});

