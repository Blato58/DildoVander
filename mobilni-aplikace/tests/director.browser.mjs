/** Compiled Vue Show journeys. All server responses are synthetic; no provider, hardware or deployment. */
import assert from 'node:assert/strict';
import {chromium} from 'playwright';
import {preview} from 'vite';
import {mkdir} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const server=await preview({root:fileURLToPath(new URL('..',import.meta.url)),preview:{host:'127.0.0.1',port:0}});
const origin=`http://127.0.0.1:${server.httpServer.address().port}`;
const output=path.join(tmpdir(),'dildovander-director-vue-browser');await mkdir(output,{recursive:true});
const evening='legacy evening',duelId='7dbf2b4b-49c6-4ab3-bb46-fcd299bb9a97';
let revision=1,duel,holdOrdinary=false,releaseOrdinary,dropStop=false;
let state={deviceId:'fixture-device',eveningId:evening,revision,phase:'idle',sessionId:'fixture-session',sessionGeneration:'g1',mood:{dominant:'amused',locked:false},tone:'dry',tempo:'standard',quiet:false,ambientEnabled:true,readyMask:63,selectionLocked:false,updatedAt:new Date().toISOString(),publicOrigin:origin};
const calls=[],errors=[],external=[];
const details={devices:[{deviceId:'fixture-device',canOperate:true}],grants:[],sessions:[],associations:[],passkeys:[],passkeyCount:1};
const json=(route,value,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(value)});
async function until(predicate,label){const deadline=Date.now()+10000;while(!predicate()){if(Date.now()>deadline)throw new Error(`Timed out: ${label}`);await new Promise(resolve=>setTimeout(resolve,10));}}
let browser;
try{
 try{browser=await chromium.launch();}catch(error){if(process.platform!=='win32')throw error;browser=await chromium.launch({channel:'chrome'});}
 const context=await browser.newContext({viewport:{width:1440,height:1000},reducedMotion:'reduce'});
 await context.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());
  if(url.origin!==origin){external.push(request.url());await route.abort();return;}
  if(url.pathname==='/app/v1/session'){await json(route,{enabled:true,account:{id:'synthetic-owner',displayName:'Obsluha fixture',roles:['Owner']},csrfToken:'synthetic-csrf',origin});return;}
  if(url.pathname==='/app/v1/account'){await json(route,details);return;}
  if(!url.pathname.startsWith('/v1/show/devices/fixture-device')){await route.continue();return;}
  const suffix=url.pathname.slice('/v1/show/devices/fixture-device'.length);
  assert.equal(request.headers()['x-dv-device-id'],'fixture-device');
  calls.push({suffix,method:request.method(),headers:request.headers(),body:request.postDataJSON()});
  if(request.method()==='GET'){
   if(!suffix)await json(route,state);
   else if(suffix==='/identities')await json(route,[{identityId:'left-id',displayName:'První host',wandId:'wand-one'},{identityId:'right-id',displayName:'Druhý host',wandId:'wand-two'}]);
   else if(suffix==='/duels')await json(route,duel?[duel]:[]);
   else if(suffix===`/duels/${duelId}`)await json(route,duel);
   else if(suffix==='/prophecies')await json(route,[{outcomeId:'outcome-one',omen:'Soukromé znamení',unlockAt:'2030-01-01T00:00:00Z',state:'locked',fullText:'DO NOT EXPOSE PRIVATE PROPHECY'}]);
   else await json(route,{code:'fixture_unknown'},404);return;
  }
  assert.equal(request.headers()['x-dv-csrf'],'synthetic-csrf');
  const body=request.postDataJSON();
  if(suffix==='/commands'){
   if(body.kind!=='stop' && body.expectedStateRevision!==revision){await json(route,{code:'state_conflict_or_visit_active'},409);return;}
   state={...state,revision:++revision,updatedAt:new Date().toISOString(),...(body.kind==='stop'?{deviceStopRevision:revision}:{})};
   const result={status:'accepted',state:structuredClone(state)};
   if(body.kind==='stop' && dropStop){dropStop=false;await route.abort();return;}
   if(body.kind!=='stop' && holdOrdinary){holdOrdinary=false;await new Promise(resolve=>{releaseOrdinary=resolve;});}
   await json(route,result);return;
  }
  if(suffix==='/duels'){
   duel={id:duelId,deviceId:'fixture-device',eveningId:evening,revision:1,state:'Ready',round:0,mode:body.mode,left:{displayName:'První host'},right:{displayName:'Druhý host'},narrationMode:'web_only',narrationStatus:'none'};await json(route,duel);return;
  }
  if(suffix.endsWith('/grants')){assert.equal(request.headers()['x-dv-confirm'],'yes');await json(route,{token:'a'.repeat(43),side:body.side,expiresAt:new Date(Date.now()+60000).toISOString(),duel});return;}
  if(suffix.endsWith('/start') || suffix.endsWith('/cancel')){duel={...duel,revision:duel.revision+1,state:suffix.endsWith('/cancel')?'Cancelled':'Running'};await json(route,duel);return;}
  assert.equal(request.headers()['x-dv-confirm'],'yes');await json(route,{status:'accepted'});
 });
 const first=await context.newPage(),second=await context.newPage();
 for(const page of [first,second]){page.setDefaultTimeout(10000);page.on('pageerror',error=>errors.push(error.message));page.on('console',message=>{if(message.type()==='error' && /Content Security Policy|script-src|Refused to execute/i.test(message.text()))errors.push(message.text());});await page.goto(`${origin}/#director`);await page.getByRole('heading',{name:'Režie kufru',exact:true}).waitFor();assert.equal(new URL(page.url()).pathname,'/suitcase/director');assert.equal(new URL(page.url()).hash,'');await page.getByRole('button',{name:'Načíst režii',exact:true}).click();await page.getByText('Revize 1',{exact:false}).waitFor();assert.equal(await page.getByLabel('Kufr účtu').inputValue(),'fixture-device');}
 assert.equal(calls.filter(call=>call.method==='POST').length,0,'mount/read never mutates');
 await first.getByRole('button',{name:'Automatická nálada',exact:true}).click();await first.getByText('Revize 2',{exact:false}).waitFor();
 await second.getByRole('button',{name:'Automatická nálada',exact:true}).click();await second.getByText('Návštěva běží nebo se změnila revize. Obnovte stav.',{exact:false}).waitFor();
 await first.getByRole('button',{name:'Načíst identity a souboje',exact:true}).click();await first.getByLabel('Levý soupeř').selectOption('left-id');await first.getByLabel('Pravý soupeř').selectOption('right-id');await first.getByLabel('Průběh').selectOption('interactive');await first.getByLabel('Oba hosté výslovně souhlasí se soubojem.').check();await first.getByRole('button',{name:'Připravit souboj',exact:true}).click();await first.getByRole('heading',{name:'První host × Druhý host'}).waitFor();
 await first.getByRole('button',{name:'Vydat odkaz',exact:true}).first().click();await first.getByRole('dialog').getByRole('button',{name:'Potvrdit změnu',exact:true}).click();const grantLink=first.getByRole('link',{name:'Odkaz pro soupeře',exact:true});await grantLink.waitFor();assert.match(await grantLink.getAttribute('href'),/#grant=a{43}&side=left$/);
 await first.getByRole('button',{name:'Načíst proroctví',exact:true}).click();await first.getByRole('heading',{name:'Soukromé znamení'}).waitFor();assert.equal(await first.getByText('DO NOT EXPOSE PRIVATE PROPHECY').count(),0);
 for(const width of [1440,390]){await first.setViewportSize({width,height:1000});await first.keyboard.press('Escape');await first.evaluate(()=>{document.activeElement?.blur();window.scrollTo(0,0);});assert(await first.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));await first.screenshot({path:path.join(output,`director-${width}.png`),fullPage:true});await first.screenshot({path:path.join(output,`director-viewport-${width}.png`)});}
 holdOrdinary=true;await first.getByRole('button',{name:'Automatická nálada',exact:true}).click();await until(()=>!!releaseOrdinary,'ordinary request held');
 await first.getByRole('button',{name:'Stop přes backend',exact:true}).click();await first.getByText(`Revize ${revision}`,{exact:false}).waitFor();const stopRevision=revision;releaseOrdinary();await first.getByText('Zneplatněno povelem Stop.',{exact:false}).waitFor();assert((await first.locator('body').innerText()).includes(`Revize ${stopRevision}`));
 dropStop=true;await first.getByRole('button',{name:'Stop přes backend',exact:true}).click();await first.getByText('Výsledek Stop přes backend je neznámý.',{exact:false}).waitFor();assert.equal(calls.filter(call=>call.body?.kind==='stop').length,2);
 await first.getByRole('link',{name:'Provoz kufru',exact:true}).click();const stops=calls.filter(call=>call.body?.kind==='stop').length;await first.getByRole('button',{name:'Stop',exact:true}).click();await until(()=>calls.filter(call=>call.body?.kind==='stop').length>stops,'global Stop request');
 assert.equal(calls.filter(call=>call.body?.kind==='stop').length,3,'global Stop retains account-bound participant after route leave');assert.deepEqual(errors,[]);assert.deepEqual(external,[]);
 console.log(JSON.stringify({result:'PASS',source:'compiled Vue + synthetic account/show API',screenshots:output,checks:['two-operator conflict','revisions+TTL','duel consent+grant confirmation','private prophecy','priority Stop+lost response','global Stop after navigation','desktop/mobile overflow'],errors:errors.length,externalRequests:external.length}));
}finally{releaseOrdinary?.();await browser?.close();await new Promise(resolve=>server.httpServer.close(resolve));}
