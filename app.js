const DB='noteit-db', STORE='tracks'; let db;

// ---- IndexedDB helpers ----
function openDB(){return new Promise((res,rej)=>{const r=indexedDB.open(DB,1);
  r.onupgradeneeded=e=>{const d=e.target.result;
    if(!d.objectStoreNames.contains(STORE)) d.createObjectStore(STORE,{keyPath:'id',autoIncrement:true});
  };
  r.onsuccess=()=>res(r.result); r.onerror=()=>rej(r.error);
});}
function addTrack(blob,name){return new Promise((res,rej)=>{const tx=db.transaction(STORE,'readwrite');
  const s=tx.objectStore(STORE); const it={name,blob,addedAt:Date.now()};
  const r=s.add(it); r.onsuccess=()=>res(r.result); r.onerror=()=>rej(r.error);
});}
function getAllTracks(){return new Promise((res,rej)=>{const tx=db.transaction(STORE,'readonly');
  const s=tx.objectStore(STORE); const r=s.getAll();
  r.onsuccess=()=>res(r.result||[]); r.onerror=()=>rej(r.error);
});}
function clearDB(){return new Promise((res,rej)=>{const tx=db.transaction(STORE,'readwrite');
  const s=tx.objectStore(STORE); const r=s.clear(); r.onsuccess=()=>res(); r.onerror=()=>rej(r.error);
});}

// ---- DOM ----
const audio=document.getElementById('audio'),
      filePicker=document.getElementById('filePicker'),
      libraryList=document.getElementById('libraryList'),
      trackTitle=document.getElementById('trackTitle'),
      trackMeta=document.getElementById('trackMeta'),
      playPauseBtn=document.getElementById('playPauseBtn'),
      nextBtn=document.getElementById('nextBtn'),
      prevBtn=document.getElementById('prevBtn'),
      shuffleBtn=document.getElementById('shuffleBtn'),
      repeatBtn=document.getElementById('repeatBtn'),
      seekBar=document.getElementById('seekBar'),
      currentTimeEl=document.getElementById('currentTime'),
      durationEl=document.getElementById('duration'),
      volumeEl=document.getElementById('volume'),
      shufflePlayAllBtn=document.getElementById('shufflePlayAllBtn'),
      clearLibBtn=document.getElementById('clearLibBtn');

// playlists UI (list of playlists + detail view rows like Library)
const playlistList = document.getElementById('playlistList');
const playlistDetail = document.getElementById('playlistDetail');
const backToPlaylists = document.getElementById('backToPlaylists');
const plTitle = document.getElementById('plTitle');
const playlistTracks = document.getElementById('playlistTracks');
const newPlaylistBtn = document.getElementById('newPlaylistBtn');
const deletePlaylistBtn = document.getElementById('deletePlaylistBtn');
const shuffleCurrentPlBtn = document.getElementById('shuffleCurrentPlBtn');

let LIBRARY=[], queue=[], currentIndex=-1;
let shuffleOn=JSON.parse(localStorage.getItem('noteit.shuffle')||'true');
let repeatOn=JSON.parse(localStorage.getItem('noteit.repeat')||'false');
let PLAYLISTS=JSON.parse(localStorage.getItem('noteit.playlists')||'[]');
if(!Array.isArray(PLAYLISTS)) PLAYLISTS=[];
if(PLAYLISTS.length===0) PLAYLISTS.push({name:'My Playlist',tracks:[]});
let activePlaylistIndex = 0;

// ---- utils & UI helpers ----
const fmt=s=>{s=Math.floor(s||0);const m=Math.floor(s/60);const sec=String(s%60).padStart(2,'0');return `${m}:${sec}`;}
function setShuffle(v){shuffleOn=v;localStorage.setItem('noteit.shuffle',JSON.stringify(v));shuffleBtn.classList.toggle('active',v);}
function setRepeat(v){repeatOn=v;localStorage.setItem('noteit.repeat',JSON.stringify(v));repeatBtn.classList.toggle('active',v);}
function savePlaylists(){localStorage.setItem('noteit.playlists', JSON.stringify(PLAYLISTS));}
function shuffleArr(a){a=[...a];for(let i=a.length-1;i>0;i--){const j=Math.floor(Math.random()*(i+1));[a[i],a[j]]=[a[j],a[i]];}return a;}

function markPlayingInLibrary(t){
  document.querySelectorAll('#libraryList li').forEach(li=>li.classList.remove('playing'));
  if(!t) return;
  const found=[...document.querySelectorAll('#libraryList li')]
    .find(li=>LIBRARY[Number(li.dataset.play)]?.url===t.url);
  if(found) found.classList.add('playing');
}

function loadTrack(t){
  audio.src=t.url;
  trackTitle.textContent=t.name;
  trackMeta.textContent='';             // no “Local file”
  audio.play().catch(()=>{});
  playPauseBtn.textContent='⏸️';
  markPlayingInLibrary(t);
}

function renderLibrary(){
  libraryList.innerHTML='';
  LIBRARY.forEach((t,i)=>{
    const li=document.createElement('li'); li.className='playable'; li.dataset.play=i;
    li.innerHTML=`<div class="title"><span class="badge">MP3</span> ${t.name}</div>
                  <div class="row-btns"><button data-add="${i}">＋ Playlist</button></div>`;
    libraryList.appendChild(li);
  });
  if(queue[currentIndex]) markPlayingInLibrary(queue[currentIndex]);
}

function renderPlaylists(){
  playlistList.innerHTML='';
  PLAYLISTS.forEach((pl,idx)=>{
    const li=document.createElement('li'); li.className='playable'; li.dataset.open=idx;
    li.innerHTML=`<div class="title"><span class="badge">PL</span> ${pl.name}
                  <span style="color:#a7b1bd">(${pl.tracks.length})</span></div>`;
    playlistList.appendChild(li);
  });
}

function renderPlaylistTracks(){
  const pl=PLAYLISTS[activePlaylistIndex]; if(!pl) return;
  plTitle.textContent=pl.name;
  playlistTracks.innerHTML='';
  pl.tracks.forEach((t,i)=>{
    const li=document.createElement('li');
    li.innerHTML=`<div class="title">${t.name}</div>
                  <div class="row-btns"><button data-playpl="${i}">Play</button>
                  <button data-delpl="${i}">Remove</button></div>`;
    playlistTracks.appendChild(li);
  });
}

// ---- boot ----
document.addEventListener('DOMContentLoaded', async()=>{
  db=await openDB();
  const rows=await getAllTracks();
  LIBRARY=rows.map(r=>({id:r.id,name:r.name,url:URL.createObjectURL(r.blob)}));
  renderLibrary(); renderPlaylists();
  setShuffle(shuffleOn); setRepeat(repeatOn);
});

// ---- events ----
filePicker.addEventListener('change', async e=>{
  const files=Array.from(e.target.files||[])
    .filter(f=>f.type.startsWith('audio')||f.name.toLowerCase().endsWith('.mp3'));
  for(const f of files){
    const buf=await f.arrayBuffer();
    const blob=new Blob([buf],{type:f.type||'audio/mpeg'});
    const id=await addTrack(blob,f.name);
    const url=URL.createObjectURL(blob);
    LIBRARY.push({id,name:f.name,url});
  }
  renderLibrary();
});

// click a row to play, or add to playlist
libraryList.addEventListener('click', e=>{
  const addBtn=e.target.closest('button[data-add]');
  if(addBtn){
    const i=Number(addBtn.dataset.add);
    const pl=PLAYLISTS[activePlaylistIndex] || PLAYLISTS[0];
    const exists=pl.tracks.some(x=>x.id===LIBRARY[i].id);
    if(exists){ alert('Already in this playlist.'); return; }
    pl.tracks.push(LIBRARY[i]); savePlaylists(); alert('Added to playlist.');
    renderPlaylistTracks(); renderPlaylists(); return;
  }
  const li=e.target.closest('li.playable'); if(!li) return;
  queue=[...LIBRARY]; if(shuffleOn) queue=shuffleArr(queue);
  const clicked=LIBRARY[Number(li.dataset.play)];
  const start=queue.findIndex(t=>t.url===clicked.url);
  currentIndex = start>=0 ? start : 0;
  loadTrack(queue[currentIndex]);
});

playPauseBtn.addEventListener('click',()=>{if(audio.paused){audio.play();playPauseBtn.textContent='⏸️';}else{audio.pause();playPauseBtn.textContent='▶️';}});
nextBtn.addEventListener('click',()=>{if(!queue.length)return;if(currentIndex<queue.length-1)currentIndex++;else if(repeatOn)currentIndex=0;else return audio.pause();loadTrack(queue[currentIndex]);});
prevBtn.addEventListener('click',()=>{if(!queue.length)return;if(currentIndex>0)currentIndex--;loadTrack(queue[currentIndex]);});
shuffleBtn.addEventListener('click',()=>setShuffle(!shuffleOn)); repeatBtn.addEventListener('click',()=>setRepeat(!repeatOn));

audio.addEventListener('loadedmetadata',()=>{seekBar.value=0;durationEl.textContent=fmt(audio.duration);});
audio.addEventListener('timeupdate',()=>{if(audio.duration){seekBar.value=(audio.currentTime/audio.duration)*100;}currentTimeEl.textContent=fmt(audio.currentTime);});
seekBar.addEventListener('input',()=>{if(audio.duration){audio.currentTime=(seekBar.value/100)*audio.duration;}}); 
volumeEl.addEventListener('input',()=>audio.volume=Number(volumeEl.value));
audio.addEventListener('ended',()=>{if(!queue.length)return;if(currentIndex<queue.length-1)currentIndex++;else if(repeatOn)currentIndex=0;else return;loadTrack(queue[currentIndex]);});

// playlists list and detail
newPlaylistBtn.addEventListener('click',()=>{const name=prompt('New playlist name:'); if(!name) return; PLAYLISTS.push({name,tracks:[]}); savePlaylists(); renderPlaylists();});
playlistList.addEventListener('click',e=>{const li=e.target.closest('li.playable'); if(!li) return; activePlaylistIndex=Number(li.dataset.open); renderPlaylistTracks(); playlistDetail.hidden=false; playlistList.hidden=true;});
backToPlaylists.addEventListener('click',()=>{playlistDetail.hidden=true; playlistList.hidden=false;});
deletePlaylistBtn.addEventListener('click',()=>{if(PLAYLISTS.length<=1) return alert('Keep at least one playlist.'); if(confirm('Delete this playlist?')){PLAYLISTS.splice(activePlaylistIndex,1); savePlaylists(); playlistDetail.hidden=true; renderPlaylists();}});
shuffleCurrentPlBtn.addEventListener('click',()=>{const pl=PLAYLISTS[activePlaylistIndex]; if(!pl?.tracks.length) return; queue = shuffleArr(pl.tracks); currentIndex=0; loadTrack(queue[0]);});

// bottom tabs
document.querySelectorAll('.tabs button').forEach(btn=>btn.addEventListener('click',()=>{
  document.querySelectorAll('.tabs button').forEach(b=>b.classList.remove('active'));
  btn.classList.add('active');
  const target=btn.dataset.tab;
  document.querySelectorAll('.tab').forEach(sec=>sec.hidden=(sec.id!==target));
  window.scrollTo({top:0,behavior:'smooth'});
}));

// SW
if('serviceWorker' in navigator){ window.addEventListener('load', ()=> navigator.serviceWorker.register('./sw.js') ); }
