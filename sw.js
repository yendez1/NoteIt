const CACHE='noteit-cache-tabs-v2';
self.addEventListener('install',(e)=>{
  e.waitUntil(caches.open(CACHE).then(c=> c.addAll([
    './','./index.html','./style.css','./app.js','./manifest.webmanifest','./assets/appicon.png'
  ])));
});
self.addEventListener('fetch',(e)=>{e.respondWith(caches.match(e.request).then(r=> r || fetch(e.request)));});
