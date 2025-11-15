const CACHE='noteit-cache-u8';
self.addEventListener('install',e=>{e.waitUntil(caches.open(CACHE).then(c=>c.addAll(['./','./index.html','./style.css','./app.js','./manifest.webmanifest','./assets/appicon.png','./assets/appicon-192.png','./assets/appicon-512.png','./assets/apple-touch-icon-180.png','./assets/favicon-32.png'])))});
self.addEventListener('fetch',e=>{e.respondWith(caches.match(e.request).then(r=>r||fetch(e.request)))});
