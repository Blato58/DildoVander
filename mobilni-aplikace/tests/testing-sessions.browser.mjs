// Compiled shared Vue application, synthetic account/domain/provider responses.
// No remote network, paid requests, real device, microphone or physical acceptance.
import assert from 'node:assert/strict';
import { readFile, stat, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
const dist=fileURLToPath(new URL('../dist/',import.meta.url));
const captures=process.env.TESTING_SCREENSHOTS ?? path.join(tmpdir(),'dildovander-testing-sessions-browser');
await stat(path.join(dist,'index.html'));await mkdir(captures,{recursive:true});
const server=createServer(async(req,res)=>{
  try{const url=new URL(req.url,'http://localhost');let file=path.resolve(dist,decodeURIComponent(url.pathname).replace(/^\//,'')||'index.html');if(!file.startsWith(dist)){res.writeHead(403).end();return;}try{if(!(await stat(file)).isFile())file=path.join(dist,'index.html');}catch{file=path.join(dist,'index.html');}
  const mime={'.html':'text/html','.js':'text/javascript','.css':'text/css','.ttf':'font/ttf','.svg':'image/svg+xml','.json':'application/json'};res.writeHead(200,{'Content-Type':mime[path.extname(file)]??'application/octet-stream','Cache-Control':'no-store'});res.end(await readFile(file));}catch{res.writeHead(500).end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const origin=`http://127.0.0.1:${server.address().port}`;
const browser=await chromium.launch(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? {executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH} : {});let page;const failures=[];const calls=[];const socketUrls=[];
try{
 const context=await browser.newContext({viewport:{width:1440,height:1000},reducedMotion:'reduce'});page=await context.newPage();page.setDefaultTimeout(10000);
 await context.addInitScript(()=>{window.microphoneRequests=0;Object.defineProperty(navigator,'mediaDevices',{configurable:true,value:{getUserMedia:()=>{window.microphoneRequests++;throw new Error('No microphone in text-only test');}}});});
 page.on('pageerror',e=>failures.push(e.message));page.on('websocket',socket=>socketUrls.push(socket.url()));page.on('console',message=>{if(message.type()==='error' && !message.text().includes('status of 504'))failures.push(message.text());});
 const owner={id:'tester-browser',displayName:'Testovací účet',roles:['Tester']};const createdAt='2026-10-24T11:00:00Z';
 let characters=[];let list=[];let detail=null;let rejectProvider=false;let chatRequests=0;let socketTurns=0;let textSocketTicket=false;
 const hardware=()=>({connected:true,ready:true,state:'ready',compartments:Array.from({length:6},(_,index)=>({slot:index+1,state:'retracted',wandName:null})),selectedCompartment:null,currentAction:'idle',ledPattern:'idle',servoCommand:null,vibrationCommand:null,cameraAvailable:true,events:[]});
 const conversation=id=>({id,ownerId:owner.id,label:'Hardware-free browser test',engine:'gpt-live-1',scenarioId:'first-visit',fixtureId:'empty',platform:'web',createdAt,endedAt:null,status:'open',recordContent:true,config:{id:'baseline',name:'Production baseline',engine:'gpt-live-1',voice:'cedar'},turns:[],events:[]});
 await context.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());if(url.origin!==origin){await route.abort();return;}const p=url.pathname,m=request.method();calls.push({path:p,method:m});const json=(value,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(value)});const body=()=>request.postDataJSON();
  if(p==='/app/v1/session')return json({enabled:true,account:owner,csrfToken:'fixture-csrf'});
  if(p==='/app/v1/account')return json({sessions:[],grants:[],associations:[],passkeys:[],devices:[]});
  if(p==='/app/v1/testing-sessions/catalog')return json({engines:['gpt-live-1','gpt-realtime-2.1'],capabilities:{text:true,voice:true,hybrid:true}});
  if(p==='/app/v1/testing-sessions/characters'){
    if(m==='POST'){const input=body();const c={id:`character-${characters.length+1}`,longTermConsent:input.longTermConsent,profile:{profileId:`profile-${characters.length+1}`,revision:1,userInput:input.userInput,status:'ready',enrichment:{recognizedName:input.userInput.name,summary:'Řízená syntetická odpověď integračního testu.',model:'controlled-provider-double',evidenceKind:'fixture',conversationIdeas:[],ambiguities:[]}}};characters.push(c);return json(c);}return json(characters);
  }
  if(p==='/app/v1/testing-sessions'){
    if(m==='POST'){const input=body();assert.equal(input.engine,'gpt-live-1');assert.equal(input.inputMode,'text');assert.equal(input.openAiConsent,true);const s={id:`session-${list.length+1}`,ownerId:owner.id,virtualDeviceId:`test-${list.length+1}`,labSessionId:`lab-${list.length+1}`,engine:input.engine,inputMode:input.inputMode,status:'open',characterId:null,createdAt,endedAt:null,revision:1};list.push(s);detail={session:s,conversation:conversation(s.labSessionId),character:null,hardware:hardware(),wand:null,keepsakes:[],memory:{status:'empty',records:[]}};return json(s);}return json(list);
  }
  if(p==='/app/v1/testing-sessions/session-1')return json(detail);
  if(p==='/app/v1/testing-sessions/session-1/character'){const c=characters.find(c=>c.id===body().characterId);assert(c);detail.character=c;detail.session.characterId=c.id;detail.session.revision++;return json(detail);}
  if(p==='/app/v1/testing-sessions/session-1/mode'){detail.session.inputMode=body().inputMode;detail.session.revision++;return json(detail.session);}
  if(p==='/app/v1/testing-sessions/session-1/duels')return json([]);
  if(p==='/app/v1/persona-lab/sessions/lab-1/socket-ticket'){textSocketTicket=body().textInputOnly===true;return json({url:origin.replace('http:','ws:')+'/app/v1/persona-lab/sessions/lab-1/live?ticket=fixture',path:'/app/v1/persona-lab/sessions/lab-1/live',expiresAt:'2026-10-24T12:00:00Z'});}
  if(p==='/app/v1/persona-lab/sessions/lab-1/turns'){
    chatRequests++;if(rejectProvider)return json({code:'lab_provider_no_reply'},504);const input=body();detail.conversation.turns.push({id:`u-${chatRequests}`,role:'user',text:input.text,createdAt},{id:`a-${chatRequests}`,role:'assistant',text:`Odpověď poskytovatelského dvojníka pro ${detail.character.profile.userInput.name}.`,createdAt,latencyMs:125});detail.conversation.events.push({id:`socket-${chatRequests}`,type:'connection_established',code:'connected',createdAt},{id:`p-${chatRequests}`,type:'provider_session',code:'ready',createdAt,providerSessionId:'controlled-provider-fixture'},{id:`r-${chatRequests}`,type:'provider_turn',code:'completed',createdAt,latencyMs:125});return json(detail.conversation);
  }
  if(p==='/app/v1/testing-sessions/session-1/select'){
    const wand={wandId:'dynamic-character-wand',physicalSlot:4,name:'Knihovní ozvěna Aleny',material:'Tis',core:'Měsíční vlákno',lore:'Řízený výstup provider double vytvořený z postavy.',temperament:'Pozorná',designDescription:'Digitální testovací návrh.',affinity:'Paměť',weakness:'Hluk',combatArchetype:'balanced',spells:['strike','trick','shield'].map(action=>({action,name:`Kouzlo ${action}`,description:'Popis testovacího kouzla.'}))};detail.wand={sessionId:'wand-generation',status:'ready',candidates:[wand],selectedWand:wand};detail.hardware.selectedCompartment=4;detail.hardware.compartments[3]={slot:4,state:'retracted',wandName:wand.name};detail.hardware.currentAction='presentation_completed';detail.hardware.servoCommand='retract:4';detail.hardware.events.push({id:'accepted-outcome',type:'presentation_completed',source:'simulation',code:'completed_by_timing',createdAt,compartment:4});return json(detail);
  }
  if(p==='/app/v1/testing-sessions/session-1/fault'){const fault=body().fault;detail.hardware.state=fault==='recover'?'ready':'fault_injected';detail.hardware.events.push({id:`fault-${fault}`,type:'fault_injected',source:'simulation',code:fault,createdAt});return json(detail);}
  if(p==='/app/v1/testing-sessions/session-1/end'){detail.session.status='ended';detail.session.endedAt=createdAt;detail.conversation.status='ended';return json(detail.session);}
  if(p.startsWith('/app/v1/')||p.startsWith('/guest/v1/'))return json({code:'unexpected_fixture_endpoint'},404);
  return route.continue();
 });
 await context.routeWebSocket(/\/app\/v1\/persona-lab\/sessions\/lab-1\/live/,socket=>{
  socket.onMessage(message=>{
    if(typeof message!=='string')return;const input=JSON.parse(message);
    if(input.type==='text'){
      socketTurns++;assert.equal(textSocketTicket,true);
      socket.send(JSON.stringify({type:'provider_ready',providerSessionId:'controlled-pcm-provider'}));
      socket.send(Buffer.from(new Int16Array(4800).fill(200).buffer));
      detail.conversation.turns.push({id:'spoken-user',role:'user',text:input.text,createdAt},{id:'spoken-assistant',role:'assistant',text:'Řízená skutečně přenesená PCM odpověď bez mikrofonu.',createdAt});
      socket.send(JSON.stringify({type:'session',session:detail.conversation}));socket.send(JSON.stringify({type:'text_ready'}));
    }else if(input.type==='cancel')socket.send(JSON.stringify({type:'interrupted'}));
  });
  socket.send(JSON.stringify({type:'ready',playbackMode:'stream',inputMode:'text'}));
 });
 await page.goto(`${origin}/testing-sessions`);await page.getByRole('heading',{name:'Začněte bez hardwaru'}).waitFor();
 assert.equal(await page.evaluate(()=>window.microphoneRequests),0);
 await page.locator('.testing-setup input[type=checkbox]').check();await page.getByRole('button',{name:'Založit test a vybrat postavu',exact:true}).click();await page.locator('#testing-character-name').waitFor();
 assert.equal(await page.locator('.testing-conversation').isVisible(),false);
 await page.locator('#testing-character-name').fill('Alena');await page.locator('#testing-character-world').fill('Knihovna měsíce');await page.getByRole('button',{name:'Uložit a dohledat postavu'}).click();await page.getByRole('heading',{name:'Alena',exact:true}).waitFor();assert.equal(characters[0].profile.userInput.description,'');
 await page.getByRole('button',{name:'Pokračovat k rozhovoru'}).click();await page.locator('#testing-message').waitFor();assert.equal(await page.locator('.testing-character').isVisible(),false);
 await page.locator('#testing-message').fill('Jsem knihovnice a hledám svou hůlku.');await page.getByRole('button',{name:'Odeslat skutečnému AI'}).click();await page.getByText('Odpověď poskytovatelského dvojníka pro Alena.',{exact:true}).waitFor();assert.equal(chatRequests,1);assert.equal(await page.evaluate(()=>window.microphoneRequests),0);
 await page.locator('#testing-message').fill('A také chráním své přátele.');await page.getByRole('button',{name:'Odeslat skutečnému AI'}).click();await page.waitForFunction(()=>document.querySelectorAll('.testing-chat li').length===4);assert.equal(chatRequests,2);
 await page.evaluate(()=>scrollTo(0,0));await page.waitForTimeout(50);await page.screenshot({path:path.join(captures,'desktop.png'),fullPage:true});
 await page.setViewportSize({width:390,height:844});await page.evaluate(()=>scrollTo(0,0));await page.waitForTimeout(50);await page.screenshot({path:path.join(captures,'mobile.png'),fullPage:true});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
 await page.setViewportSize({width:960,height:900});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
 await page.setViewportSize({width:320,height:760});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));await page.locator('#testing-message').focus();assert.equal(await page.locator('#testing-message').evaluate(e=>document.activeElement===e),true);
 await page.getByRole('button',{name:'Pokračovat k výsledku'}).click();await page.getByRole('button',{name:'Vybrat hůlku skutečným AI'}).click();await page.getByRole('heading',{name:'Knihovní ozvěna Aleny',exact:true}).waitFor();assert.equal(await page.locator('.virtual-compartments .is-selected strong').textContent(),'4');
 await page.reload();await page.locator('#testing-message').waitFor();assert.equal(await page.locator('.testing-chat li').count(),4);assert.equal(chatRequests,2);assert.equal(new URL(page.url()).searchParams.get('session'),'session-1');assert.equal(await page.evaluate(()=>window.microphoneRequests),0);
 await page.locator('.testing-steps button').nth(0).click();await page.locator('.testing-setup input[type=checkbox]').check();await page.locator('#testing-input').selectOption('hybrid');await page.locator('.testing-steps button').nth(2).click();await page.getByRole('button',{name:'Zapnout mikrofon a hlasové spojení'}).waitFor();assert.equal(await page.evaluate(()=>window.microphoneRequests),0);
 await page.locator('.testing-steps button').nth(0).click();await page.locator('#testing-input').selectOption('text');await page.locator('.testing-steps button').nth(2).click();await page.locator('#testing-message').waitFor();
 rejectProvider=true;await page.locator('#testing-message').fill('Ověř chybu poskytovatele.');await page.getByRole('button',{name:'Odeslat skutečnému AI'}).click();await page.locator('.aw-notice--danger').filter({hasText:'lab_provider_no_reply'}).waitFor();assert.equal(await page.locator('.testing-chat li').count(),4);
 rejectProvider=false;await page.locator('.testing-conversation .testing-consent input').check();await page.locator('#testing-message').fill('Odpověz nahlas, mikrofon nemám.');await page.getByRole('button',{name:'Odeslat skutečnému AI'}).click();await page.getByText('Řízená skutečně přenesená PCM odpověď bez mikrofonu.',{exact:true}).waitFor();assert.equal(socketTurns,1);assert.equal(textSocketTicket,true);assert.equal(await page.evaluate(()=>window.microphoneRequests),0);
 assert(!calls.some(call=>/start-tickets|hardware-ticket|\/devices\//.test(call.path)));assert.deepEqual(failures,[]);console.log(JSON.stringify({passed:true,checks:['create_without_hardware','character_real_contract','typed_backend_adapter_no_microphone','accepted_virtual_event','dynamic_wand','resume_no_duplicate_turn','modality_switch','provider_failure_no_fake_reply','desktop_mobile_320px','keyboard_focus','typed_spoken_pcm_no_microphone'],screenshots:captures,provider:'controlled double; no paid calls',physicalAcceptance:false},null,2));
}catch(error){console.error('Synthetic browser failure:',await page?.locator('.aw-notice').allTextContents(),calls.filter(call=>call.path.includes('ticket')),socketUrls,failures);throw error;}finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
