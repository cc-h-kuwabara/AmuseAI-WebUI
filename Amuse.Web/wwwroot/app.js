const $ = id => document.getElementById(id);
const titles = { generate:'生成スタジオ',process:'メディア加工',editor:'画像エディター',video:'動画エディター',gallery:'ギャラリー',jobs:'ジョブ・履歴',models:'モデル',environments:'実行環境',settings:'設定' };
const workflows = {TextToImage:'テキスト → 画像',ImageToImage:'画像 → 画像',ImageEdit:'指示による画像編集',ImageInpaint:'画像のInpaint',PaintToImage:'ペイント → 画像',TextToVideo:'テキスト → 動画',ImageToVideo:'画像 → 動画',VideoToVideo:'動画 → 動画',FrameToFrame:'動画の各フレームを変換',TextToAudio:'テキスト → 音声',TextToMusic:'音楽生成',AudioToText:'音声 → テキスト',TextInstruct:'テキスト生成',TextConverse:'会話',ImageToText:'画像 → テキスト'};
const stateNames = {queued:'待機中',running:'実行中',canceling:'キャンセル中',canceled:'キャンセル済み',completed:'完了',failed:'失敗',interrupted:'中断'};
let token = '', catalog = null, media = [], jobs = [], lastShown = '', configValue = null, noticeTimer;
let pollBusy = false, currentConfig = '', clips = [];
function el(tag, text, cls) { const node = document.createElement(tag); if(text !== undefined) node.textContent = text; if(cls) node.className = cls; return node; }
function notice(message, error=false) { $('notice').textContent = message; $('notice').className = error?'error':''; $('notice').hidden = false; clearTimeout(noticeTimer); if(!error) noticeTimer=setTimeout(()=>$('notice').hidden=true,7000); }
function guard(fn) { return async (...args) => { try { return await fn(...args); } catch(error) { notice(error.message, true); } }; }
async function api(path, options={}) {
  const headers = {...options.headers};
  if(options.body && !(options.body instanceof FormData)) headers['Content-Type']='application/json';
  if(options.method && !['GET','HEAD'].includes(options.method)) headers['X-Amuse-Token']=token;
  const response = await fetch(`/api${path}`,{...options,headers});
  if(!response.ok) { let message = `${response.status} ${response.statusText}`; try { message = (await response.json()).error || message; } catch {} throw new Error(message); }
  return response.status === 204 || response.headers.get('content-length') === '0' ? null : response.json();
}
function setOptions(id, values, selected) {
  const target=$(id), previous=selected ?? [...target.selectedOptions].map(x=>x.value);
  target.replaceChildren(...values.map(([value,text])=>{ const o=el('option',text);o.value=value; o.selected=previous.includes(String(value));return o; }));
}
const selected = id => [...$(id).selectedOptions].map(o=>o.value).filter(Boolean);
function route() {
  const page=location.hash.slice(1) || 'generate';
  if(!titles[page]) { location.hash='generate'; return; }
  for(const section of document.querySelectorAll('.page')) section.hidden=section.id!==page;
  document.querySelectorAll('[data-page]').forEach(a=>a.classList.toggle('active',a.dataset.page===page));
  $('page-title').textContent=titles[page];
  if(page==='settings') guard(loadConfig)();
  if(page==='models') guard(loadModels)();
}
async function loadCatalog() {
  catalog=await api('/catalog');
  const devices=catalog.devices.map(d=>[d.id,`${d.name} · ${d.memory} MB`]);
  for(const id of ['device','process-device']) setOptions(id,devices);
  if(!devices.length) notice('利用可能なGPUが見つかりません。ドライバーとGPU環境を確認してください。'+(catalog.hardwareError?`\n${catalog.hardwareError}`:''),true);
  setOptions('loras',catalog.loras.map(m=>[m.id,m.name]));
  setOptions('tools',(catalog.tools||[]).map(t=>[t.name,t.displayName]));
  setOptions('control-model',[['','使用しない'],...catalog.controlNets.map(m=>[m.id,m.name])]);
  updateModels(); updateProcessModels(); renderEnvironments();
}
function updateModels(reset=false) {
  if(!catalog)return;
  const previous=$('model').value;
  const view=$('workflow').value;
  const process=({PaintToImage:'ImageToImage',FrameToFrame:'ImageToImage',TextInstruct:'TextToText',TextConverse:'TextToText',TextToMusic:'TextToAudio'})[view]||view;
  const models=catalog.models.filter(m=>(!m.viewFilter?.length || m.viewFilter.includes(view))&&(!m.processTypes?.length||m.processTypes.includes(process)));
  setOptions('model',models.map(m=>[`${m.kind}:${m.id}`,`${m.name} · ${m.backend} [${m.status}]`]));
  $('generate-button').disabled=!models.length || !catalog.devices.length;
  if(!models.length) $('model-hint').textContent='このワークフローのモデルがありません。モデル設定で追加してください。';
  else if(reset || $('model').value!==previous) applyDefaults();
  else { const model=currentModel();$('model-hint').textContent=`${model.pipeline} · ${model.mediaType} · ${model.status}`;updateQuality(); }
  $('input-details').open=!['TextToImage','TextToVideo','TextToAudio','TextToMusic','TextInstruct'].includes(view);
  renderConversation();
}
function currentModel(){return catalog?.models.find(m=>`${m.kind}:${m.id}`===$('model').value);}
function restoreRequest(){
  const raw=localStorage.getItem('amuse-last-request');if(!raw)return;
  try{
    const request=JSON.parse(raw),options=request.options||{};
    if(!workflows[request.view])return;
    $('workflow').value=request.view;updateModels(true);
    const model=`${request.modelKind}:${request.modelId}`;
    if(!Array.from($('model').options).some(o=>o.value===model))return;
    $('model').value=model;applyDefaults();
    if(Array.from($('device').options).some(o=>o.value===String(request.deviceId)))$('device').value=request.deviceId;
    updateQuality();if(Array.from($('quality').options).some(o=>o.value===request.quality))$('quality').value=request.quality;
    $('memory').value=request.memory||'Auto';$('count').value=request.count||1;
    const fields={prompt:'prompt',negative:'negativePrompt',width:'width',height:'height',steps:'steps',seed:'seed',guidance:'guidanceScale',frames:'frames',fps:'frameRate',duration:'duration',instruction:'instruction','max-length':'maxLength'};
    for(const [id,key]of Object.entries(fields))if(options[key]!==undefined&&options[key]!==null)$(id).value=options[key];
    if(request.view!=='TextConverse'||!localStorage.getItem('amuse-conversation'))$('conversation').value=JSON.stringify(options.conversation||[],null,2);
    const known=new Set([...Object.values(fields),'conversation']);
    $('advanced').value=JSON.stringify(Object.fromEntries(Object.entries(options).filter(([key])=>!known.has(key))),null,2);
    const inputs={'input-images':request.images,'input-controls':request.controls,'input-audios':request.audios,'input-videos':request.videos,loras:request.loras,tools:request.tools};
    if(request.view==='ImageInpaint'&&request.images?.length>1){inputs['input-images']=request.images.slice(0,-1);$('input-mask').value=request.images.at(-1);}
    for(const [id,values]of Object.entries(inputs))for(const option of $(id).options)option.selected=(values||[]).map(String).includes(option.value);
    $('control-model').value=request.controlNetId??'';renderConversation();
  }catch{notice('保存した生成条件を復元できませんでした。モデルと入力値を確認してください。',true);}
}
function applyDefaults(){
  const model=currentModel();if(!model)return;
  const d=model.defaultOptions || {};
  const map={width:'width',height:'height',steps:'steps',guidance:'guidanceScale',frames:'frames',fps:'frameRate',duration:'duration','max-length':'maxLength'};
  for(const [id,key] of Object.entries(map)) $(id).value=d[key] ?? 0;
  $('instruction').value=''; $('advanced').value='{}';
  $('model-hint').textContent=`${model.pipeline} · ${model.mediaType} · ${model.status}`;
  updateQuality();
}
function updateQuality(){const device=catalog?.devices.find(d=>d.id===+$('device').value),model=currentModel();const modes=(device?.qualityModes||['Standard','Draft','Production']).filter(q=>!model?.memoryProfile?.length||model.memoryProfile.some(p=>p.qualityMode===q));setOptions('quality',modes.map(q=>[q,q]));}
function updateProcessModels(){
  if(!catalog)return;
  const operation=$('operation').value;
  setOptions('process-model',(operation==='upscale'?catalog.upscalers:operation==='extract'?catalog.extractors:[]).map(m=>[m.id,`${m.name} [${m.status}]`]));
  $('process-model').disabled=operation==='interpolate';
}
async function loadFiles(){ media=await api('/files'); renderFiles(); }
function renderFiles(){
  const subset=type=>media.filter(f=>f.contentType.startsWith(type)).map(f=>[f.id,f.name]);
  for(const id of ['input-images','input-controls']) setOptions(id,subset('image/'));
  setOptions('input-mask',[['','使用しない'],...subset('image/')]);
  setOptions('input-audios',subset('audio/'));setOptions('input-videos',subset('video/'));
  const visual=media.filter(f=>/^(image|video)\//.test(f.contentType)).map(f=>[f.id,f.name]);
  setOptions('process-file',visual);setOptions('video-source',visual);
  setOptions('video-audio',[['','使用しない'],...subset('audio/')]);
  $('file-count').textContent=`${media.length} ファイル`;
  const grid=$('gallery-grid');grid.replaceChildren();
  if(!media.length){grid.append(el('p','まだファイルがありません。生成するか、ファイルを追加してください。','muted'));return;}
  for(const file of media){
    const card=el('article',undefined,'file-card'),thumb=el('div',undefined,'thumbnail');
    const node=previewNode(file,true);thumb.append(node);
    const info=el('div',undefined,'file-info');info.append(el('p',file.name));
    const link=el('a','保存');link.href=`/api/files/${file.id}?download=true`;info.append(link);
    if(file.contentType.startsWith('image/')){
      const edit=el('button','画像を編集');edit.onclick=guard(async()=>{await canvasLoad(`/api/files/${file.id}`);location.hash='editor';});info.append(edit);
      const use=el('button','入力に使う');use.onclick=()=>selectInput(file);info.append(use);
    }
    if(file.contentType.startsWith('text/')||file.contentType==='application/json'){
      const open=el('button','表示');open.onclick=guard(()=>showText(file));info.append(open);
    }
    const remove=el('button','ごみ箱へ');remove.onclick=guard(async()=>{await api(`/files/${file.id}`,{method:'DELETE'});await loadFiles();notice('ごみ箱に移動しました。ごみ箱から復元できます。');});info.append(remove);
    card.append(thumb,info);grid.append(card);
  }
}
function previewNode(file,small=false){
  const url=`/api/files/${file.id}`;let node;
  if(file.contentType.startsWith('image/')){node=el('img');node.src=url;node.alt=file.name;node.loading='lazy';}
  else if(file.contentType.startsWith('video/')){node=el('video');node.src=url;node.controls=true;node.preload='metadata';}
  else if(file.contentType.startsWith('audio/')){node=el('audio');node.src=url;node.controls=true;node.preload='metadata';}
  else {node=el('span',file.contentType==='application/json'?'{}':'T','file-icon');}
  return node;
}
async function showText(file){$('text-content').textContent=await (await fetch(`/api/files/${file.id}`)).text();$('text-dialog').showModal();}
function selectInput(file){const id=file.contentType.startsWith('image/')?'input-images':file.contentType.startsWith('video/')?'input-videos':'input-audios';for(const o of $(id).options)o.selected=o.value===file.id;$('input-details').open=true;location.hash='generate';notice('生成の入力に設定しました。');}
async function upload(fileList){
  if(!fileList?.length)return [];
  const form=new FormData();for(const f of fileList)form.append('files',f);
  const result=await api('/files',{method:'POST',body:form});await loadFiles();notice(`${result.length} ファイルを追加しました。`);return result;
}
async function submitGenerate(event){
  event.preventDefault(); const model=currentModel(); if(!model)throw new Error('モデルを選択してください。');
  const overrides=JSON.parse($('advanced').value || '{}');
  if(!overrides || Array.isArray(overrides)||typeof overrides!=='object')throw new Error('詳細パラメーターはJSONオブジェクトで指定してください。');
  const options={prompt:$('prompt').value,negativePrompt:$('negative').value,width:+$('width').value,height:+$('height').value,
    steps:+$('steps').value,seed:+$('seed').value,guidanceScale:+$('guidance').value,frames:+$('frames').value,frameRate:+$('fps').value,
    duration:+$('duration').value,instruction:$('instruction').value,maxLength:+$('max-length').value,conversation:JSON.parse($('conversation').value||'[]'),...overrides};
  const request={modelKind:model.kind,modelId:model.id,view:$('workflow').value,deviceId:+$('device').value,
    quality:$('quality').value,memory:$('memory').value,count:+$('count').value,options,images:selected('input-images'),controls:selected('input-controls'),
    audios:selected('input-audios'),videos:selected('input-videos'),loras:selected('loras').map(Number),controlNetId:$('control-model').value?+$('control-model').value:null};
  request.tools=selected('tools');
  if(request.view==='ImageInpaint' && $('input-mask').value) request.images=[...request.images.filter(id=>id!==$('input-mask').value),$('input-mask').value];
  const job=await api('/generate',{method:'POST',body:JSON.stringify(request)});
  if(request.view==='TextConverse'){localStorage.setItem('amuse-conversation-job',job.id);localStorage.setItem('amuse-conversation-prompt',request.options.prompt);}
  localStorage.setItem('amuse-last-request',JSON.stringify(request));
  notice('生成をキューに追加しました。');await poll();
}
async function submitProcess(event){event.preventDefault();const job=await api('/process',{method:'POST',body:JSON.stringify({operation:$('operation').value,modelId:+$('process-model').value,deviceId:+$('process-device').value,file:$('process-file').value,options:$('process-options').value.trim()?JSON.parse($('process-options').value):null,multiplier:+$('multiplier').value})});notice('処理をキューに追加しました。');await poll();}
function jobNode(job){
  const card=el('article',undefined,'job'),header=el('div',undefined,'job-header');card.dataset.jobId=job.id;header.append(el('span',workflows[job.kind]||job.kind,'job-title'),el('span',stateNames[job.status]||job.status,`state ${job.status}`));card.append(header,el('small',new Date(job.created).toLocaleString()));
  if(job.message)card.append(el('p',job.message));
  if(job.status==='running'||job.status==='canceling'){const p=el('progress');if(job.maximum>0){p.max=job.maximum;p.value=job.value;}card.append(p);}
  if(job.error)card.append(el('p',job.error,'error'));
  const actions=el('div',undefined,'job-buttons');
  if(['queued','running','canceling'].includes(job.status)){
    const cancel=el('button','キャンセル');cancel.disabled=job.status==='canceling';cancel.onclick=guard(async()=>{await api(`/jobs/${job.id}/cancel`,{method:'POST'});await poll();});actions.append(cancel);
  }
  for(const id of job.files||[]){const f=media.find(x=>x.id===id);const a=el('a',f?.name.endsWith('.recipe.json')?'設定を保存':`結果 ${actions.childNodes.length+1}`);a.href=`/api/files/${id}?download=true`;actions.append(a);}
  card.append(actions);return card;
}
async function poll(){
  if(pollBusy)return;pollBusy=true;
  try{
    const previous=new Map(jobs.map(j=>[j.id,j.status]));jobs=await api('/jobs');
    const finished=jobs.filter(j=>j.status==='completed' && previous.get(j.id)!=='completed');
    if(finished.length)await loadFiles();
    for(const job of jobs.filter(j=>j.status==='completed'&&j.id===localStorage.getItem('amuse-conversation-job'))){
      const conversation=media.find(f=>job.files?.includes(f.id)&&f.name.endsWith('.conversation.json'));
      if(conversation){$('conversation').value=JSON.stringify(await(await fetch(`/api/files/${conversation.id}`)).json(),null,2);localStorage.setItem('amuse-conversation',$('conversation').value);if($('prompt').value===localStorage.getItem('amuse-conversation-prompt'))$('prompt').value='';localStorage.removeItem('amuse-conversation-job');renderConversation();}
    }
    if(finished.some(j=>j.kind.startsWith('settings:')||j.kind==='download'||j.kind.startsWith('environment:')))await loadCatalog();
    $('queue-count').textContent=jobs.filter(j=>['queued','running','canceling'].includes(j.status)).length;
    const list=$('job-list');list.replaceChildren(...jobs.map(jobNode));if(!jobs.length)list.append(el('p','まだジョブはありません。','muted'));
    const current=jobs.filter(j=>['queued','running','canceling'].includes(j.status));
    $('current-jobs').replaceChildren(...(current.length?current.slice(-4):jobs.slice(0,2)).map(jobNode));
    if(!jobs.length)$('current-jobs').append(el('p','キューは空です。','muted'));
    const running=jobs.find(j=>j.status==='running'&&(j.previewVersion||j.streamText));
    if(running){
      const result=$('result');
      if(running.streamText)result.replaceChildren(el('pre',running.streamText));
      else if(result.dataset.preview!==`${running.id}:${running.previewVersion}`){const img=el('img');img.src=`/api/jobs/${running.id}/preview?v=${running.previewVersion}`;img.alt='生成中のプレビュー';result.replaceChildren(img);result.dataset.preview=`${running.id}:${running.previewVersion}`;}
      $('result-status').textContent='GENERATING';lastShown='';
    }
    const latest=jobs.find(j=>j.status==='completed'&&j.files?.some(id=>media.find(f=>f.id===id&&f.contentType!=='application/json')));
    if(!running && latest && latest.id!==lastShown){lastShown=latest.id;const file=media.find(f=>f.id===latest.files.find(id=>media.find(f=>f.id===id&&f.contentType!=='application/json')));if(file){const result=$('result');result.replaceChildren();if(file.contentType.startsWith('text/'))result.append(el('pre',await(await fetch(`/api/files/${file.id}`)).text()));else result.append(previewNode(file));$('result-status').textContent='COMPLETED';}}
    if(jobs.some(j=>j.status==='running'||j.status==='canceling'))$('result-status').textContent='GENERATING';
    $('connection').textContent=location.host;
  }finally{pollBusy=false;}
}
async function loadModels(){
  const kind=$('model-category').value,items=await api(`/config/${kind}`),search=$('model-search').value.toLowerCase();
  const list=$('model-list');list.replaceChildren();
  for(const m of items.filter(m=>(m.name||'').toLowerCase().includes(search))){
    const card=el('article',undefined,'model-card'),info=el('div');info.append(el('h3',m.name||m.key||'Unnamed'),el('small',[m.backend,m.pipeline,m.status].filter(Boolean).join(' · ')));
    const button=el('button','ダウンロード');button.onclick=guard(async()=>{await api(`/models/${kind}/${m.id}/download`,{method:'POST'});notice('ダウンロードをキューに追加しました。');await poll();});card.append(info,button);list.append(card);
  }
}
function renderEnvironments(){
  const list=$('environment-list');list.replaceChildren();
  for(const [index,item] of (catalog?.environments||[]).entries()){
    const card=el('article',undefined,'environment-card'),info=el('div');info.append(el('h3',item.name||item.environment),el('small',[item.backend,item.vendor,item.pipeline,item.status].filter(Boolean).join(' · ')));
    const actions=el('div',undefined,'actions');for(const [op,text] of [['create','作成'],['update','更新'],['rebuild','再構築']]){
      const b=el('button',text);b.onclick=guard(async()=>{if(op==='rebuild'&&!confirm('この環境を再構築しますか？'))return;await api(`/environments/${index}/${op}`,{method:'POST'});notice('環境処理をキューに追加しました。');await poll();});actions.append(b);
    }card.append(info,actions);list.append(card);
  }
}
async function loadConfig(){
  currentConfig=$('config-section').value;configValue=await api(`/config/${currentConfig}`);
  const fields=$('settings-fields');fields.replaceChildren();$('config-advanced').hidden=currentConfig==='tokens';
  if(currentConfig==='tokens'){
    for(const item of configValue){const label=el('label',`${item.name} (${item.domain}) · ${item.configured?'設定済み':'未設定'}`),input=el('input');input.type='password';input.autocomplete='new-password';input.dataset.token=item.name;input.placeholder='変更するときだけ入力';label.append(input);const clear=el('input');clear.type='checkbox';clear.dataset.clearToken=item.name;const clearLabel=el('label','保存済みトークンを消去');clearLabel.className='token-label';clearLabel.prepend(clear);fields.append(label,clearLabel);}
  }else if(currentConfig==='general'){
    const labels={directoryModel:'モデル保存先',historyItems:'履歴の最大表示数',readBuffer:'読み込みバッファー',writeBuffer:'書き込みバッファー',videoCodec:'動画コーデック',isDiffusionImagePreviewEnabled:'生成プレビューを有効にする',isServerDebugEnabled:'AIホストのデバッグ表示',isLegacyDeviceDetection:'旧方式でGPUを検出（再起動後に反映）',defaultDeviceId:'既定GPU ID',isExternalToolsEnabled:'AIツールの外部アクセスを有効にする',isExternalToolsAcknowledged:'ツールがデータを外部に送信する場合があることを確認した'};
    for(const [key,value] of Object.entries(configValue)){
      const label=el('label',labels[key]||key),input=el('input');input.dataset.setting=key;
      if(typeof value==='boolean'){input.type='checkbox';input.checked=value;}else{input.type=typeof value==='number'?'number':'text';input.value=value??'';}label.append(input);fields.append(label);
    }
  }else{fields.append(el('p','モデルの追加・パス・対応ワークフローなど、すべての設定を以下で編集できます。','hint'));$('config-advanced').open=true;}
  $('config-json').value=JSON.stringify(configValue,null,2);
}
async function saveConfig(){
  let value;
  if(currentConfig==='tokens')value=[...document.querySelectorAll('[data-token]')].map(input=>({name:input.dataset.token,token:[...document.querySelectorAll('[data-clear-token]')].find(x=>x.dataset.clearToken===input.dataset.token).checked?'':input.value||null}));
  else if(currentConfig==='general'){
    value=JSON.parse($('config-json').value);for(const input of document.querySelectorAll('[data-setting]'))value[input.dataset.setting]=input.type==='checkbox'?input.checked:input.type==='number'?+input.value:input.value;
  }else value=JSON.parse($('config-json').value);
  await api(`/config/${currentConfig}`,{method:'PUT',body:JSON.stringify(value)});for(const input of document.querySelectorAll('[data-token]'))input.value='';notice('設定の保存をキューに追加しました。結果はジョブで確認できます。');await poll();
}

// Canvas editor: original media stays intact; every saved revision is a new file.
const canvas=$('canvas'),ctx=canvas.getContext('2d',{willReadFrequently:true}),undo=[];let drawing=false,lastPoint=null;
function checkpoint(){undo.push(ctx.getImageData(0,0,canvas.width,canvas.height));if(undo.length>12)undo.shift();}
function canvasPoint(event){const r=canvas.getBoundingClientRect();return {x:(event.clientX-r.left)*canvas.width/r.width,y:(event.clientY-r.top)*canvas.height/r.height};}
function stroke(point){const size=+$('canvas-size').value;ctx.globalCompositeOperation=$('canvas-tool').value==='erase'?'destination-out':'source-over';ctx.strokeStyle=$('canvas-color').value;ctx.fillStyle=ctx.strokeStyle;ctx.lineWidth=size;ctx.lineCap='round';ctx.lineJoin='round';ctx.beginPath();ctx.moveTo(lastPoint.x,lastPoint.y);ctx.lineTo(point.x,point.y);ctx.stroke();lastPoint=point;ctx.globalCompositeOperation='source-over';}
canvas.addEventListener('pointerdown',event=>{checkpoint();const p=canvasPoint(event);if($('canvas-tool').value==='text'){const text=prompt('追加するテキスト');if(text){ctx.fillStyle=$('canvas-color').value;ctx.font=`${Math.max(12,+$('canvas-size').value*2)}px sans-serif`;ctx.fillText(text,p.x,p.y);}return;}drawing=true;lastPoint=p;canvas.setPointerCapture(event.pointerId);stroke({x:p.x+.01,y:p.y+.01});});
canvas.addEventListener('pointermove',event=>{if(drawing)stroke(canvasPoint(event));});
for(const event of ['pointerup','pointercancel','lostpointercapture'])canvas.addEventListener(event,()=>drawing=false);
async function canvasLoad(url){const image=new Image();image.src=url;await image.decode();checkpoint();ctx.drawImage(image,0,0,canvas.width,canvas.height);}
async function saveCanvas(use=false){const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));if(!blob)throw new Error('画像を書き出せませんでした。');const result=await upload([new File([blob],`canvas-${Date.now()}.png`,{type:'image/png'})]);if(use)selectInput(result[0]);}
$('canvas-file').onchange=guard(async event=>{const file=event.target.files[0];if(!file)return;const url=URL.createObjectURL(file);try{await canvasLoad(url);}finally{URL.revokeObjectURL(url);event.target.value='';}});
$('canvas-undo').onclick=()=>{const image=undo.pop();if(image){if(canvas.width!==image.width||canvas.height!==image.height){canvas.width=image.width;canvas.height=image.height;}ctx.putImageData(image,0,0);$('canvas-width').value=canvas.width;$('canvas-height').value=canvas.height;}};
$('canvas-clear').onclick=()=>{checkpoint();ctx.clearRect(0,0,canvas.width,canvas.height);};
$('canvas-mask').onclick=()=>{checkpoint();ctx.fillStyle='#000';ctx.fillRect(0,0,canvas.width,canvas.height);$('canvas-color').value='#ffffff';$('canvas-tool').value='draw';};
$('canvas-resize').onclick=guard(()=>{const width=+$('canvas-width').value,height=+$('canvas-height').value;if(!Number.isInteger(width)||!Number.isInteger(height)||width<64||height<64||width>8192||height>8192)throw new Error('サイズは64〜8192の整数を指定してください。');checkpoint();const copy=document.createElement('canvas');copy.width=canvas.width;copy.height=canvas.height;copy.getContext('2d').drawImage(canvas,0,0);canvas.width=width;canvas.height=height;ctx.drawImage(copy,0,0,width,height);});
$('canvas-save').onclick=guard(()=>saveCanvas());$('canvas-to-input').onclick=guard(()=>saveCanvas(true));

function renderClips(){const list=$('clips');list.replaceChildren();clips.forEach((clip,index)=>{const row=el('div',undefined,'clip');row.append(el('strong',media.find(f=>f.id===clip.file)?.name||clip.file));for(const [key,title] of [['start','開始（秒）'],['duration','長さ（秒）']]){const label=el('label',title),input=el('input');input.type='number';input.min=key==='start'?'0':'0.1';input.step='0.1';input.value=clip[key];input.onchange=()=>clip[key]=+input.value;label.append(input);row.append(label);}for(const [text,action] of [['↑',()=>{if(index>0)[clips[index-1],clips[index]]=[clips[index],clips[index-1]];}],['↓',()=>{if(index<clips.length-1)[clips[index+1],clips[index]]=[clips[index],clips[index+1]];}],['削除',()=>clips.splice(index,1)]]){const b=el('button',text);b.onclick=()=>{action();renderClips();};row.append(b);}list.append(row);});}
$('video-add').onclick=()=>{if($('video-source').value){clips.push({file:$('video-source').value,start:0,duration:3});renderClips();}};
$('video-upload').onchange=guard(async event=>{for(const file of await upload(event.target.files))clips.push({file:file.id,start:0,duration:3});renderClips();event.target.value='';});
$('video-export').onclick=guard(async()=>{if(!clips.length)throw new Error('クリップを追加してください。');await api('/compose',{method:'POST',body:JSON.stringify({clips,width:+$('video-width').value,height:+$('video-height').value,frameRate:+$('video-fps').value,audio:$('video-audio').value||null})});notice('動画書き出しをキューに追加しました。');await poll();});

setOptions('workflow',Object.entries(workflows));
$('workflow').onchange=()=>updateModels(true);$('model').onchange=applyDefaults;$('operation').onchange=updateProcessModels;
$('device').onchange=updateQuality;
$('generate-form').onsubmit=guard(submitGenerate);$('process-form').onsubmit=guard(submitProcess);
for(const id of ['input-upload','process-upload','gallery-upload'])$(id).onchange=guard(async event=>{const result=await upload(event.target.files);if(id==='input-upload')for(const f of result){const target=f.contentType.startsWith('image/')?'input-images':f.contentType.startsWith('video/')?'input-videos':'input-audios';for(const o of $(target).options)if(o.value===f.id)o.selected=true;}if(id==='process-upload'&&result[0])$('process-file').value=result[0].id;event.target.value='';});
$('refresh').onclick=guard(async()=>{await loadCatalog();await loadFiles();await poll();if(location.hash==='#models')await loadModels();notice('更新しました。');});
$('model-category').onchange=guard(loadModels);$('model-search').oninput=guard(loadModels);
$('model-edit').onclick=()=>{$('config-section').value=$('model-category').value;location.hash='settings';};
$('config-section').onchange=guard(loadConfig);$('config-save').onclick=guard(saveConfig);
$('close-dialog').onclick=()=>$('text-dialog').close();window.addEventListener('hashchange',route);
function renderConversation(){const visible=$('workflow').value==='TextConverse';$('conversation-clear').hidden=!visible;const transcript=$('conversation-transcript');transcript.replaceChildren();if(!visible)return;try{for(const message of JSON.parse($('conversation').value||'[]')){const card=el('div',undefined,'job');card.append(el('small',message.role||message.Role),el('p',message.content||message.Content||''));transcript.append(card);}}catch{}}
$('conversation').value=localStorage.getItem('amuse-conversation')||'[]';
$('conversation-clear').onclick=()=>{$('conversation').value='[]';localStorage.removeItem('amuse-conversation');localStorage.removeItem('amuse-conversation-job');renderConversation();};
$('show-trash').onclick=guard(async()=>{const list=$('trash-list');list.hidden=!list.hidden;if(list.hidden)return;const deleted=await api('/trash');list.replaceChildren(el('h2','ごみ箱'));for(const file of deleted){const row=el('div',undefined,'model-card');row.append(el('span',file.name));const restore=el('button','復元');restore.onclick=guard(async()=>{await api(`/trash/${file.id}/restore`,{method:'POST'});await loadFiles();row.remove();});row.append(restore);list.append(row);}if(!deleted.length)list.append(el('p','ごみ箱は空です。','muted'));});
await guard(async()=>{token=(await api('/session')).token;await loadCatalog();await loadFiles();restoreRequest();await poll();route();setInterval(()=>guard(poll)(),1800);})();
