// tabs build with playlists and click-to-play
const DB='noteit-db', STORE='tracks'; let db;
function openDB(){return new Promise((res,rej)=>{const r=indexedDB.open(DB,1);r.onupgradeneeded=e=>{const d=e.target.result;if(!d.objectStoreNames.contains(STORE))d.createObjectStore(STORE,{keyPath:'id',autoIncrement:true});};r.onsuccess=()=>res(r.result);r.onerror=()=>rej(r.error);});}
function addTrack(blob,name){return new Promise((res,rej)=>{const tx=db.transaction(STORE,'readwrite');const s=tx.objectStore(STORE);const it={name,blob,addedAt:Date.now()};const r=s.add(it);r.onsuccess=()=>res(r.result);r.onerror=()=>rej(r.error);});}
function getAllTracks(){return new Promise((res,rej)=>{const tx=db.transaction(STORE,'readonly');const s=tx.objectStore(STORE);const r=s.getAll();r.onsuccess=()=>res(r.result||[]);r.onerror=()=>rej(r.error);});}
function clearDB(){return new Promise((res,rej)=>{const tx=db.transaction(STORE,'readwrite');const s=tx.objectStore(STORE);const r=s.clear();r.onsuccess=()=>res();r.onerror=()=>rej(r.error);});}

const audio=document.getElementById('audio');
const filePicker=document.getElementById('filePicker');
const libraryList=document.getElementById('libraryList');
const playlistSelect=document.getElementById('playlistSelect');
const playlistTracks=document.getElementById('playlistTracks');
const trackTitle=document.getElementById('trackTitle');
const trackMeta=document.getElementById('trackMeta');
const playPauseBtn=document.getElementById('playPauseBtn');
const nextBtn=document.getElementById('nextBtn');
const prevBtn=document.getElementById('prevBtn');
const shuffleBtn=document.getElementById('shuffleBtn');
const repeatBtn=document.getElementById('repeatBtn');
const seekBar=document.getElementById('seekBar');
const currentTimeEl=document.getElementById('currentTime');
const durationEl=document.getElementById('duration');
const volumeEl=document.getElementById('volume');
const playAllBtn=document.getElementById('playAllBtn');
const clearLibBtn=document.getElementById('clearLibBtn');
const newPlaylistBtn=document.getElementById('newPlaylistBtn');
const deletePlaylistBtn=document.getElementById('deletePlaylistBtn');
const shuffleCurrentPlBtn=document.getElementById('shuffleCurrentPlBtn');

let LIBRARY=[]; let queue=[]; let currentIndex=-1;
let shuffleOn=JSON.parse(localStorage.getItem('noteit.shuffle')||'false');
let repeatOn=JSON.parse(localStorage.getItem('noteit.repeat')||'false');
let PLAYLISTS=JSON.parse(localStorage.getItem('noteit.playlists')||'[]'); if(!Array.isArray(PLAYLISTS)) PLAYLISTS=[];
if(PLAYLISTS.length===0) PLAYLISTS.push({name:'My Playlist',tracks:[]});
const fmt=s=>{s=Math.floor(s||0);const m=Math.floor(s/60);const sec=String(s%60).padStart(2,'0');return `${m}:${sec}`;}
function setShuffle(v){shuffleOn=v;localStorage.setItem('noteit.shuffle',JSON.stringify(v));shuffleBtn.classList.toggle('active',v);}
function setRepeat(v){repeatOn=v;localStorage.setItem('noteit.repeat',JSON.stringify(v));repeatBtn.classList.toggle('active',v);}
function savePlaylists(){localStorage.setItem('noteit.playlists',JSON.stringify(PLAYLISTS));}

function loadTrack(t){audio.src=t.url;trackTitle.textContent=t.name;trackMeta.textContent='Local file';audio.play().catch(()=>{});playPauseBtn.textContent='⏸️';}
function shuffleArr(a){a=[...a];for(let i=a.length-1;i>0;i--){const j=Math.floor(Math.random()*(i+1));[a[i],a[j]]=[a[j],a[i]];}return a;}
function renderLibrary(){libraryList.innerHTML='';LIBRARY.forEach((t,i)=>{const li=document.createElement('li');li.className='playable';li.dataset.play=i;li.innerHTML=`<div class="title"><span class="badge">MP3</span> ${t.name}</div><div class="row-btns"><button data-add="${i}">＋ Playlist</button></div>`;libraryList.appendChild(li);});}
function renderPlaylistUI(){playlistSelect.innerHTML='';PLAYLISTS.forEach((p,i)=>{const o=document.createElement('option');o.value=i;o.textContent=p.name;playlistSelect.appendChild(o);}); renderPlaylistTracks();}
function renderPlaylistTracks(){const idx=Number(playlistSelect.value||0);const pl=PLAYLISTS[idx];playlistTracks.innerHTML='';pl.tracks.forEach((t,i)=>{const li=document.createElement('li');li.innerHTML=`<div class="title">${t.name}</div><div class="row-btns"><button data-playpl="${i}">Play</button><button data-delpl="${i}">Remove</button></div>`;playlistTracks.appendChild(li);});}

document.addEventListener('DOMContentLoaded',async()=>{db=await openDB();const rows=await getAllTracks();LIBRARY=rows.map(r=>({id:r.id,name:r.name,url:URL.createObjectURL(r.blob)}));renderLibrary();renderPlaylistUI();setShuffle(shuffleOn);setRepeat(repeatOn);});
filePicker.addEventListener('change',async e=>{const files=Array.from(e.target.files||[]).filter(f=>f.type.startsWith('audio')||f.name.toLowerCase().endsWith('.mp3'));for(const f of files){const buf=await f.arrayBuffer();const blob=new Blob([buf],{type:f.type||'audio/mpeg'});const id=await addTrack(blob,f.name);const url=URL.createObjectURL(blob);LIBRARY.push({id,name:f.name,url});}renderLibrary();});

libraryList.addEventListener('click',e=>{const btn=e.target.closest('button[data-add]');if(btn){const i=Number(btn.dataset.add);const idx=Number(playlistSelect.value||0);PLAYLISTS[idx].tracks.push(LIBRARY[i]);savePlaylists();renderPlaylistTracks();return;}const li=e.target.closest('li.playable');if(li){queue=[...LIBRARY];if(shuffleOn) queue=shuffleArr(queue);currentIndex=Math.max(0, queue.findIndex(t=>t.url===LIBRARY[Number(li.dataset.play)].url)); loadTrack(queue[currentIndex]);}});

playlistTracks.addEventListener('click',e=>{const btn=e.target.closest('button');if(!btn)return;const idx=Number(playlistSelect.value||0);const pl=PLAYLISTS[idx];if(btn.dataset.playpl){queue=[...pl.tracks];if(shuffleOn) queue=shuffleArr(queue);currentIndex=Number(btn.dataset.playpl);loadTrack(queue[currentIndex]);}else if(btn.dataset.delpl){pl.tracks.splice(Number(btn.dataset.delpl),1);savePlaylists();renderPlaylistTracks();}});

playPauseBtn.addEventListener('click',()=>{if(audio.paused){audio.play();playPauseBtn.textContent='⏸️';}else{audio.pause();playPauseBtn.textContent='▶️';}});
nextBtn.addEventListener('click',()=>{if(!queue.length)return;if(currentIndex<queue.length-1)currentIndex++;else if(repeatOn)currentIndex=0;else return audio.pause();loadTrack(queue[currentIndex]);});
prevBtn.addEventListener('click',()=>{if(!queue.length)return;if(currentIndex>0)currentIndex--;loadTrack(queue[currentIndex]);});
shuffleBtn.addEventListener('click',()=>setShuffle(!shuffleOn)); repeatBtn.addEventListener('click',()=>setRepeat(!repeatOn));
audio.addEventListener('loadedmetadata',()=>{seekBar.value=0;durationEl.textContent=fmt(audio.duration);});
audio.addEventListener('timeupdate',()=>{if(audio.duration){seekBar.value=(audio.currentTime/audio.duration)*100;}currentTimeEl.textContent=fmt(audio.currentTime);});
seekBar.addEventListener('input',()=>{if(audio.duration){audio.currentTime=(seekBar.value/100)*audio.duration;}});
volumeEl.addEventListener('input',()=>audio.volume=Number(volumeEl.value));
audio.addEventListener('ended',()=>{if(!queue.length)return;if(currentIndex<queue.length-1)currentIndex++;else if(repeatOn)currentIndex=0;else return;loadTrack(queue[currentIndex]);});

playAllBtn.addEventListener('click',()=>{queue=[...LIBRARY];if(shuffleOn) queue=shuffleArr(queue);currentIndex=0;loadTrack(queue[0]);});
clearLibBtn.addEventListener('click',async()=>{if(confirm('Clear your local library?')){await clearDB();LIBRARY=[];renderLibrary();queue=[];currentIndex=-1;trackTitle.textContent='No track selected';trackMeta.textContent='—';audio.removeAttribute('src');}});

newPlaylistBtn.addEventListener('click',()=>{const name=prompt('New playlist name:');if(!name)return;PLAYLISTS.push({name,tracks:[]});savePlaylists();renderPlaylistUI();playlistSelect.value=String(PLAYLISTS.length-1);renderPlaylistTracks();});
deletePlaylistBtn.addEventListener('click',()=>{if(PLAYLISTS.length<=1)return alert('Keep at least one playlist.');const idx=Number(playlistSelect.value||0);if(confirm('Delete this playlist?')){PLAYLISTS.splice(idx,1);savePlaylists();renderPlaylistUI();}});
shuffleCurrentPlBtn.addEventListener('click',()=>{const idx=Number(playlistSelect.value||0);const pl=PLAYLISTS[idx];if(!pl.tracks.length)return;queue=shuffleArr(pl.tracks);currentIndex=0;loadTrack(queue[0]);});

document.querySelectorAll('.tabs button').forEach(b=>b.addEventListener('click',()=>{document.querySelectorAll('.tabs button').forEach(x=>x.classList.remove('active'));b.classList.add('active');document.querySelectorAll('.tab').forEach(sec=>sec.hidden = sec.id!==b.dataset.tab);window.scrollTo({top:0,behavior:'smooth'});}));

let deferredPrompt; const installBtn=document.getElementById('installBtn');
window.addEventListener('beforeinstallprompt',e=>{e.preventDefault();deferredPrompt=e;installBtn.style.display='inline-flex';});
installBtn.addEventListener('click',async()=>{if(!deferredPrompt)return alert('Open in Safari and tap Share → Add to Home Screen');deferredPrompt.prompt();await deferredPrompt.userChoice;deferredPrompt=null;});
if('serviceWorker' in navigator){window.addEventListener('load',()=>navigator.serviceWorker.register('./sw.js'));}
