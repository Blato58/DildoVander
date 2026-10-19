import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {runInNewContext} from 'node:vm';
import {test} from 'node:test';
const html=await readFile(new URL('../index.html',import.meta.url),'utf8');
const script=html.match(/<script>([\s\S]*?)<\/script>/)[1];
function bootstrap(path,hash){
 const events=new Map(),location={pathname:path,search:'?duel=fixture',hash};const window={addEventListener:(event,handler)=>events.set(event,handler)};
 const document={createElement:()=>({}),head:{append(){}}};
 const history={replaceState(_state,_title,url){assert.equal(url,path+location.search);location.hash='';}};
 runInNewContext(script,{location,window,document,history});return {window,location,events};
}
test('early duel capture scrubs exact validated grant before modules, including router trailing-slash aliases',()=>{
 for(const path of ['/archive/duel','/archive/duel/','/keepsake/duel.html']){
  const value=bootstrap(path,`#grant=${'a'.repeat(43)}&side=left`);assert.equal(value.location.hash,'');assert.equal(value.window.__dildovanderDuelGrant.token,'a'.repeat(43));assert.equal(value.window.__dildovanderDuelGrant.side,'left');assert.equal(value.window.__dildovanderGuestClaim,undefined);value.events.get('pagehide')();assert.equal(value.window.__dildovanderDuelGrant,undefined);
 }
});
test('malformed, duplicate and extra duel fields are scrubbed without admission',()=>{
 for(const hash of [`#grant=${'a'.repeat(43)}&side=left&side=right`,`#grant=${'a'.repeat(43)}&side=left&extra=1`,'#grant=short&side=left']){
  const value=bootstrap('/archive/duel',hash);assert.equal(value.location.hash,'');assert.equal(value.window.__dildovanderDuelGrant,undefined);
 }
});
test('nonguest Director deep link survives bootstrap for explicit router migration',()=>{
 const value=bootstrap('/','#director');assert.equal(value.location.hash,'#director');assert.equal(value.window.__dildovanderDuelGrant,undefined);
});
