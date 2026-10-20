// Real CAD viewer + native firmware + isolated production fake backend.
// Chromium fake devices are mandatory: this never captures a user's hardware.
const {chromium}=require('playwright');
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const crypto=require('node:crypto');

async function main(){
  const [origin,credentialFile,output]=process.argv.slice(2);
  assert(origin&&credentialFile&&output,'origin/private fixture credentials/output required');
  assert(new URL(origin).hostname==='localhost'||process.env.KUFR_TEST_VPS_PROOF==='true','explicit isolated VPS proof opt-in');
  const credentials=JSON.parse(fs.readFileSync(credentialFile,'utf8'));
  fs.mkdirSync(output,{recursive:true});
  const browser=await chromium.launch({headless:true,
    ...(process.env.KUFR_TEST_BROWSER?{executablePath:process.env.KUFR_TEST_BROWSER}:{channel:'chrome'}),
    args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream']});
  const context=await browser.newContext({viewport:{width:1400,height:900}});
  const page=await context.newPage();
  const errors=[],requests=[],drains=[],pcm=[],photos=[];
  page.on('pageerror',error=>errors.push(error.message));
  page.on('request',request=>{if(request.url().includes('/v1/twin/'))requests.push({path:new URL(request.url()).pathname,method:request.method(),bytes:request.postDataBuffer()?.length||0,
    ...(request.url().endsWith('/media/start')?{admission:JSON.parse(request.postData())}:{})});});
  page.on('response',async response=>{
    if(response.url().endsWith('/media/photo')&&response.status()===204){photos.push(Number((await response.allHeaders())['x-twin-media-accepted-bytes']));}
    if(response.url().endsWith('/media/output')&&response.status()===200){
      const frame=await response.json();
      if(frame.kind==='pcm')pcm.push(frame.byteLength);
      if(frame.kind==='drain')drains.push({epoch:frame.epoch,outputEpoch:frame.outputEpoch,pcmBytes:frame.pcmBytes});
    }
  });
  const route='/v1/twin/instances/sim-integration';
  let claimed=false;
  const command=async body=>{
    const response=await context.request.post(origin+route+'/commands',{headers:{Origin:origin},data:body});
    assert.equal(response.status(),200,await response.text());return response.json();
  };
  try{
    const login=await context.request.post(origin+'/v1/twin/session',{headers:{Origin:origin},data:{credential:credentials.integration}});
    assert.equal(login.status(),200);
    await command({action:'stop'});await command({action:'inspect'});claimed=true;
    await page.goto(origin+'/twin');
    await page.locator('#twin-details > summary').click();
    await page.locator('#twin-refresh').click();
    await page.locator('#twin-source').selectOption('sim-integration');
    await page.waitForFunction(()=>document.querySelector('#twin-badge').textContent.includes('ONLINE'));
    await page.locator('[data-media-settings]').locator('..').locator('summary').click();
    await page.locator('[data-media-engine]').selectOption('fake');
    await page.locator('[data-media-voice]').selectOption('marin');
    await page.locator('[data-media-variant]').selectOption('main');
    await page.locator('[data-media-consent]').check();
    const settingsResponse=page.waitForResponse(response=>response.url().endsWith('/settings')&&response.request().method()==='PUT');
    await page.locator('[data-media-save]').click();
    assert.equal((await settingsResponse).status(),200);
    const savedSettings=await (await context.request.get(origin+route+'/settings')).json();
    assert.equal(savedSettings.engine,'fake','paid providers must remain disabled for acceptance');
    if(new URL(origin).hostname!=='localhost'){
      assert(savedSettings.availability.realtime&&savedSettings.availability.gptLive,'authorized existing server key enables both production provider settings');
      assert.equal(await page.locator('[data-media-engine] option[value="realtime"]').isDisabled(),false);
      assert.equal(await page.locator('[data-media-engine] option[value="gpt-live"]').isDisabled(),false);
    }
    await page.locator('[data-media-speaker]').click();await page.locator('[data-media-mic]').click();
    await page.locator('[data-media-start]').waitFor({state:'visible'});
    await page.waitForFunction(()=>!document.querySelector('[data-media-start]').disabled);
    const matrices=()=>page.evaluate(()=>model.meshes.filter(mesh=>/^tray_[1-6]$/.test(mesh.name)).map(mesh=>({name:mesh.name,z:F2.movement(mesh)[14]})));
    const before=await matrices();
    await page.locator('[data-media-start]').click();
    await page.locator('[data-media-camera]').waitFor({state:'visible'});
    assert.equal(requests.filter(request=>request.path.endsWith('/media/photo')).length,0);
    await page.locator('[data-media-camera]').click();
    await page.waitForFunction(()=>document.querySelector('[data-media-video]').videoWidth>0);
    await page.locator('[data-media-capture]').click();
    await page.locator('[data-media-preview]').waitFor({state:'visible'});
    // Explicit fake-camera diagnostic fixture stays private, outside recordings.
    const fakeJpeg=await page.locator('[data-media-preview]').evaluate(async image=>Array.from(new Uint8Array(await (await fetch(image.src)).arrayBuffer())));
    fs.writeFileSync(path.join(output,'fake-camera-fixture.jpg'),Buffer.from(fakeJpeg));
    await page.screenshot({path:path.join(output,'desktop-photo-preview.png')});
    assert.equal(requests.filter(request=>request.path.endsWith('/media/photo')).length,0,'camera preview is local until acceptance');
    await page.locator('[data-media-accept]').click();
    await page.waitForFunction(()=>F2.state().trayAngles.some(angle=>angle>100),undefined,{timeout:45000});
    const current=await (await context.request.get(origin+route)).json();
    const slot=current.snapshot.reported.slot;
    const after=await matrices();
    const changes=after.map((item,index)=>({name:item.name,z:item.z-before[index].z}));
    assert.equal(changes.find(item=>item.name===`tray_${slot}`).z,16,'actual firmware-selected CAD tray lifted16mm');
    assert(changes.filter(item=>item.name!==`tray_${slot}`).every(item=>item.z===0),'other actual CAD trays stayed resting');
    assert.equal(current.snapshot.reported.physicalVerification,'unavailable');
    assert.equal(drains.length,5);assert.equal(pcm.length,5);assert(pcm.every(bytes=>bytes===4800));
    assert.equal(requests.filter(request=>request.path.endsWith('/media/photo')).length,1);
    assert.equal(photos.length,1);assert(photos[0]>12&&photos[0]<=262144,'server confirms actual JPEG bytes, not an unavailable Playwright Blob body');
    const mediaState=await (await context.request.get(origin+route+'/media/state')).json();
    assert.equal(mediaState.photoWarning,null,'native photo path completed without capture/upload fallback');
    assert.equal(requests.filter(request=>request.path.endsWith('/media/cancel')).length,0,'normal completion never cancels presentation');
    const mediaErrors=await page.locator('[data-media-error]').textContent();assert.equal(mediaErrors,'');
    await page.locator('#twin-details > summary').click();
    await page.screenshot({path:path.join(output,'desktop-native-voice-raised.png')});
    await page.setViewportSize({width:390,height:844});
    await page.screenshot({path:path.join(output,'mobile-native-voice-raised.png')});
    assert(await page.locator('#twin-badge').isVisible());
    assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
    const boot=current.snapshot.source.bootId;
    await page.reload();await page.locator('#twin-details > summary').click();await page.locator('#twin-refresh').click();
    await page.locator('#twin-source').selectOption('sim-integration');
    await page.waitForFunction(()=>F2.state().trayAngles.some(angle=>angle>100));
    const reloaded=await (await context.request.get(origin+route)).json();
    assert.equal(reloaded.snapshot.source.bootId,boot,'viewer reload never boots device');
    assert.equal(requests.filter(request=>request.path.endsWith('/media/start')).length,1,'reload never repeats input');
    await command({action:'stop'});
    const stopped=await (await context.request.get(origin+route)).json();
    assert.equal(stopped.snapshot.reported.needsRecovery,true);assert.equal(stopped.snapshot.reported.physicalVerification,'unavailable');
    assert.equal(stopped.snapshot.model.slots[slot-1].position,1,'Stop never invents return to rest');
    // Explicit service recovery sets the virtual rest assumption for a new test.
    // Reload during pending photo must close only that media actor, not the host.
    await command({action:'inspect'});
    await page.locator('[data-media-speaker]').click();await page.locator('[data-media-mic]').click();
    await page.locator('[data-media-start]').click();
    await page.locator('[data-media-camera]').waitFor({state:'visible'});
    const activeReloadStarted=performance.now();
    await page.reload();
    let cancelledState;
    for(let attempt=0;attempt<100;attempt++){
      cancelledState=await (await context.request.get(origin+route+'/media/state')).json();
      if(!cancelledState.active)break;
      await page.waitForTimeout(50);
    }
    assert.equal(cancelledState.active,false,'keepalive cancels actual native actor after active reload');
    const activeReloadCancelMs=performance.now()-activeReloadStarted;
    const activeReloadSnapshot=await (await context.request.get(origin+route)).json();
    assert.equal(activeReloadSnapshot.snapshot.source.bootId,boot,'active reload never reboots native device');
    assert.equal(activeReloadSnapshot.snapshot.reported.slot,0,'cancelled photo workflow never admits presentation');
    await page.locator('#twin-details > summary').click();await page.locator('#twin-refresh').click();
    await page.locator('#twin-source').selectOption('sim-integration');
    await page.locator('[data-media-speaker]').click();await page.locator('[data-media-mic]').click();
    await page.locator('[data-media-start]').click();
    await page.locator('[data-media-camera]').waitFor({state:'visible'});
    const finalCancelResponse=page.waitForResponse(response=>response.url().endsWith('/media/cancel'));
    await page.locator('[data-media-stop]').click();
    assert.equal((await finalCancelResponse).status(),204,'new explicit media start succeeds after reload cancellation');
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(output,'browser-media-result.json'),JSON.stringify({origin,buildId:current.snapshot.source.buildId,
      bootId:boot,selectedSlot:slot,cadRevision:current.snapshot.source.cadManifestRevision,matrices:changes,
      fakeBrowserDevices:true,providerMode:'fake',providerAvailability:savedSettings.availability,
      microphoneInputs:requests.filter(request=>request.path.endsWith('/media/input')).map(request=>request.bytes),
      photoBytes:photos[0],photoWarning:mediaState.photoWarning,pcmBytes:pcm,drains,
      bootPreserved:true,reloadNoReplay:true,stopNoAutoLower:true,activeReloadCancelsMedia:true,activeReloadCancelMs,
      newConversationAfterActiveReload:true,physicalVerification:'unavailable',pageErrors:errors,
      desktopViewport:[1400,900],mobileViewport:[390,844]},null,2));
    console.log('PASS actual CAD + native firmware + isolated fake microphone/photo/audio/drain/proposal; mobile/reload/Stop; no paid calls');
  }catch(error){
    if(!page.isClosed())console.log('media status',await page.locator('[data-media-status]').textContent({timeout:1000}).catch(()=>''),await page.locator('[data-media-error]').textContent({timeout:1000}).catch(()=>''));
    const stateResponse=await context.request.get(origin+route+'/media/state').catch(()=>null);
    const state=stateResponse?.ok()?await stateResponse.json():null;
    const status=state?Object.fromEntries(['phase','active','inputEnabled','error','photoWarning'].map(key=>[key,state[key]])):null;
    fs.writeFileSync(path.join(output,'failure-metadata.json'),JSON.stringify({requests,drains,pcm,photos,errors,state:status},null,2));
    await page.screenshot({path:path.join(output,'failure-fake-browser.png')}).catch(()=>{});
    console.log('native media state',status);
    throw error;
  }finally{
    if(claimed){await command({action:'stop'}).catch(()=>{});await command({action:'release'}).catch(()=>{});}
    await browser.close();
  }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
