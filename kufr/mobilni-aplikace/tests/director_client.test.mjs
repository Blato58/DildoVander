import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {stripTypeScriptTypes} from 'node:module';
import {test} from 'node:test';
const source=stripTypeScriptTypes(await readFile(new URL('../src/features/director/DirectorClient.ts',import.meta.url),'utf8'),{mode:'transform'});
const {DirectorClient,DirectorRequestError}=await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
test('account client confines device/path/header, never forwards bearer or arbitrary origin',async()=>{
 let sent;const client=new DirectorClient(async(path,init)=>{sent={path,...init};return Response.json({revision:4});},'case.s3');
 assert.deepEqual(await client.request('/commands',{kind:'stop'}),{revision:4});
 assert.equal(sent.path,'/v1/show/devices/case.s3/commands');assert.equal(sent.headers['X-DV-Device-Id'],'case.s3');assert.equal(sent.headers.Authorization,undefined);assert.equal(sent.headers['X-DV-Confirm'],undefined);
 for(const path of ['/../commands','https://evil.test','/device-cues','/duels?eveningId=bad&extra=1','/commands?extra=1'])await assert.rejects(client.request(path));
 client.forget();await assert.rejects(client.request());
});
test('sensitive mutation requires explicit confirmation; routine Stop remains low friction',async()=>{
 let count=0,headers;const client=new DirectorClient(async(_path,init)=>{count++;headers=init.headers;return Response.json({ok:true});},'fixture');
 await assert.rejects(client.request('/end-evening',{}),error=>error instanceof DirectorRequestError && error.code==='confirmation_required');assert.equal(count,0);
 await client.request('/end-evening',{},true);assert.equal(headers['X-DV-Confirm'],'yes');
 await client.request('/commands',{kind:'stop'});assert.equal(headers['X-DV-Confirm'],undefined);
});
test('context switch aborts and fences delayed response body; conflict never retries',async()=>{
 let resolveBody;let signal;const client=new DirectorClient(async(_path,init)=>{signal=init.signal;return {ok:true,text:()=>new Promise(resolve=>{resolveBody=resolve;})};},'first');
 const pending=client.request();await Promise.resolve();client.bind('second');assert.equal(signal.aborted,true);resolveBody('{"secret":"old"}');await assert.rejects(pending,error=>error.name==='AbortError');
 let count=0;const conflict=new DirectorClient(async()=>{count++;return Response.json({code:'state_conflict_or_visit_active'},{status:409});},'fixture');
 await assert.rejects(conflict.request('/commands',{}),error=>error instanceof DirectorRequestError && error.status===409);assert.equal(count,1);
});
