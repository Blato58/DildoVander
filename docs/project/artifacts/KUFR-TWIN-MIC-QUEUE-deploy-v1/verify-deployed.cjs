// Isolated sim-integration acceptance. Usage: node verify-deployed.cjs origin credentialFile outputDir expectedBuildId
// Credentials and the private settings backup must stay outside tracked artifacts.
const {chromium}=require('playwright');
const fs=require('node:fs');
const path=require('node:path');

class VerificationError extends Error {
  constructor(code,status) { super(code); this.code=code; this.status=status; }
}
function check(value,code) { if (!value) throw new VerificationError(code); }
function metadata(error) {
  return error instanceof VerificationError ? {code:error.code,...(error.status?{status:error.status}:{})}
    : {code:error?.name==='TimeoutError'?'browser_timeout':'verification_exception'};
}
function outside(file,directory) {
  const relative=path.relative(directory,path.resolve(file));
  return relative==='..' || relative.startsWith('..'+path.sep) || path.isAbsolute(relative);
}
const writable=value=>Object.fromEntries(['engine','voice','speed','photoConsent','presentationVariant'].map(key=>[key,value[key]]));
const sameSettings=(left,right)=>JSON.stringify(writable(left))===JSON.stringify(writable(right));

async function main() {
  const [suppliedOrigin,credentialFile,outputDir,expectedBuildId]=process.argv.slice(2);
  check(suppliedOrigin&&credentialFile&&outputDir&&expectedBuildId,'four_arguments_required');
  const url=new URL(suppliedOrigin), origin=url.origin;
  check(suppliedOrigin.replace(/\/$/,'')===origin&&!url.username&&!url.password,'plain_origin_required');
  check(url.hostname==='localhost'||process.env.KUFR_TEST_VPS_PROOF==='true','explicit_vps_proof_required');
  check(url.protocol==='https:'||url.hostname==='localhost'&&url.protocol==='http:','https_required');
  const artifactsRoot=path.resolve(__dirname,'..');
  const backupFile=path.resolve(outputDir,'..','private-original-settings.json');
  check(outside(credentialFile,artifactsRoot)&&outside(backupFile,artifactsRoot),'private_files_outside_artifacts_required');
  const credentials=JSON.parse(fs.readFileSync(credentialFile,'utf8'));
  check(Object.keys(credentials).length===1&&typeof credentials.integration==='string'&&credentials.integration.length>0,'integration_credential_only');
  fs.mkdirSync(outputDir,{recursive:true});
  // Never overwrite a recovery backup from an earlier interrupted execution.
  check(!fs.existsSync(backupFile),'existing_private_backup_requires_review');
  const base='/v1/twin/instances/sim-integration';
  const inputs=[],pcm=[],drains=[],photos=[],responseTasks=[],pageErrors=[];
  const results={expectedBuildId,instanceId:'sim-integration',providerMode:'fake',fakeBrowserDevices:true,
    delayRequestedMs:600,settingsRestored:false,controlsReleased:false,browserClosed:false,optionalActiveReload:'not_run'};
  let browser,context,page,original,authenticated=false,primaryError=null;
  const cleanupErrors=[];
  let delayed=false,delayMs=0,cancelCount=0,startCount=0,normalComplete=false;
  let recordPrimaryMedia=true;
  let captureMaximum=0;

  async function request(method,suffix,data) {
    const response=await context.request[method](origin+base+suffix,{headers:{Origin:origin},...(data===undefined?{}:{data})});
    if (!response.ok()) throw new VerificationError('http_'+method+'_'+suffix.replace(/[^a-z]/g,'_'),response.status());
    return response.status()===204?null:response.json();
  }
  const command=action=>request('post','/commands',{action});
  async function until(read,predicate,code,timeout=60000) {
    const deadline=Date.now()+timeout;
    do {
      const value=await read();
      if (predicate(value)) return value;
      await new Promise(resolve=>setTimeout(resolve,100));
    } while (Date.now()<deadline);
    throw new VerificationError(code);
  }
  const idle=()=>until(()=>request('get',''),value=>value.snapshot.reported.state==='idle','native_idle_timeout');
  async function selectSource() {
    if (!await page.locator('#twin-details').evaluate(element=>element.open)) await page.locator('#twin-details > summary').click();
    await page.locator('#twin-refresh').click();
    await page.locator('#twin-source').selectOption('sim-integration');
    await page.waitForFunction(()=>document.querySelector('#twin-badge')?.textContent.includes('ONLINE'));
  }
  async function activateAndStart() {
    await page.locator('[data-media-speaker]').click();
    await page.locator('[data-media-mic]').click();
    await page.waitForFunction(()=>!document.querySelector('[data-media-start]').disabled);
    await page.locator('[data-media-start]').click();
    await page.locator('[data-media-camera]').waitFor({state:'visible'});
  }
  async function collectCapture() {
    const value=await page.evaluate(()=>({captureMaximum:window.__testCaptureMaximum||0,
      delayApplied:window.__testDelayApplied===true,delayMs:window.__testDelayMs||0}));
    captureMaximum=Math.max(captureMaximum,value.captureMaximum);
    delayed=delayed||value.delayApplied;
    delayMs=Math.max(delayMs,value.delayMs);
  }
  try {
    results.stage='browser_launch';
    browser=await chromium.launch({channel:'chrome',headless:true,
      args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream']});
    context=await browser.newContext({viewport:{width:1400,height:900}});
    results.stage='authenticate_and_save_original_settings';
    const login=await context.request.post(origin+'/v1/twin/session',{headers:{Origin:origin},data:{credential:credentials.integration}});
    check(login.status()===200,'session_authentication_failed');
    authenticated=true;
    original=await request('get','/settings');
    fs.writeFileSync(backupFile,JSON.stringify({...writable(original),revision:original.revision},null,2),{flag:'wx',mode:0o600});
    results.stage='prepare_isolated_fake_settings';
    await command('stop');
    await command('inspect');
    await idle();
    const current=await request('get','/settings');
    const saved=await request('put','/settings',{expectedRevision:current.revision,engine:'fake',voice:'marin',speed:1,
      presentationVariant:'main',photoConsent:true});
    check(saved.engine==='fake'&&saved.voice==='marin'&&saved.speed===1&&saved.photoConsent===true&&saved.presentationVariant==='main','fake_settings_not_applied');

    await context.addInitScript(()=>{
      let media;
      window.__testCaptureMaximum=0;
      window.__testDelayApplied=false;
      window.__testDelayMs=0;
      // Fetch-only interception preserves the browser's native JPEG Blob transport.
      const originalFetch=window.fetch.bind(window);
      window.fetch=function(input,options) {
        const requestUrl=new URL(typeof input==='string'||input instanceof URL?input:input.url,location.href);
        const method=options?.method||(input instanceof Request?input.method:'GET');
        if (!window.__testDelayApplied&&method.toUpperCase()==='POST'&&requestUrl.origin===location.origin
          &&requestUrl.pathname==='/v1/twin/instances/sim-integration/media/input') {
          window.__testDelayApplied=true;
          const started=performance.now();
          return new Promise(resolve=>setTimeout(resolve,600)).then(()=>{
            window.__testDelayMs=performance.now()-started;
            return originalFetch(input,options);
          });
        }
        return originalFetch(input,options);
      };
      const observeWarning=()=>{
        if (window.__testJitterObserved) return;
        const status=document.querySelector('[data-media-status]');
        const state=window.__testMedia?.state();
        if (status?.textContent.includes('část neodeslaného zvuku byla přeskočena')&&state?.actor&&state.mic
          &&document.querySelector('[data-media-error]')?.textContent==='') {
          window.__testJitterObserved={whileActive:true,visible:status.getClientRects().length>0,captureBytes:state.captureBytes};
        }
      };
      new MutationObserver(observeWarning).observe(document,{subtree:true,childList:true,characterData:true});
      Object.defineProperty(window,'TwinMedia',{configurable:true,get:()=>media,set(value) {
        const mount=value.mount;
        value.mount=function(...args) {
          const mounted=mount.apply(this,args);
          window.__testMedia=mounted;
          const sample=()=>{ window.__testCaptureMaximum=Math.max(window.__testCaptureMaximum,mounted.state().captureBytes); };
          window.setInterval(sample,5);
          sample();
          return mounted;
        };
        media=value;
      }});
    });
    page=await context.newPage();
    page.on('pageerror',()=>pageErrors.push('page_error'));
    page.on('request',item=>{
      const pathname=new URL(item.url()).pathname;
      if (pathname===base+'/media/cancel') cancelCount++;
      if (pathname===base+'/media/start') startCount++;
      if (recordPrimaryMedia&&pathname===base+'/media/input') {
        const headers=item.headers();
        inputs.push({epoch:headers['input-epoch'],sequence:headers['input-sequence'],status:null,acceptedBytes:null});
      }
    });
    page.on('response',response=>{
      const pathname=new URL(response.url()).pathname;
      if (!recordPrimaryMedia) return;
      if (![base+'/media/input',base+'/media/photo',base+'/media/output'].includes(pathname)) return;
      const task=(async()=>{
        if (pathname===base+'/media/input') {
          const headers=response.request().headers();
          const item=inputs.find(value=>value.epoch===headers['input-epoch']&&value.sequence===headers['input-sequence']&&value.status===null);
          if (item) {
            item.status=response.status();
            item.acceptedBytes=Number((await response.allHeaders())['x-twin-media-accepted-bytes']||0);
            if (response.status()===409) {
              const problem=await response.json();
              // This single known code is an expected VAD retirement race.
              item.retired=problem.code==='media-input-retired';
            }
          }
        } else if (pathname===base+'/media/photo') {
          photos.push({status:response.status(),acceptedBytes:Number((await response.allHeaders())['x-twin-media-accepted-bytes']||0)});
        } else if (response.status()===200) {
          const frame=await response.json();
          if (frame.kind==='pcm') pcm.push(frame.byteLength);
          if (frame.kind==='drain') drains.push({pcmBytes:frame.pcmBytes});
        }
      })();
      // Keep asynchronous observer failures until the main flow can report them safely.
      responseTasks.push(task.then(()=>null,()=>({code:'media_response_observer_failed'})));
    });
    results.stage='load_actual_cad_and_select_source';
    await page.goto(origin+'/twin');
    await selectSource();
    await page.waitForFunction(()=>typeof model!=='undefined'&&model.twinManifest?.revision&&model.meshes.filter(mesh=>/^tray_[1-6]$/.test(mesh.name)).length===6);
    const initial=await request('get','');
    check(initial.snapshot.source.buildId===expectedBuildId,'deployed_build_mismatch');
    results.buildId=initial.snapshot.source.buildId;
    results.cadRevision=initial.snapshot.source.cadManifestRevision;
    results.bootId=initial.snapshot.source.bootId;
    const cad=await page.evaluate(()=>({revision:model.twinManifest.revision,hardwareProfile:model.twinManifest.hardwareProfile,
      trays:model.meshes.filter(mesh=>/^tray_[1-6]$/.test(mesh.name)).length}));
    check(cad.revision===results.cadRevision&&cad.hardwareProfile===initial.snapshot.source.hardwareProfile,'cad_manifest_mismatch');
    check((await page.locator('#twin-badge').textContent()).includes('CAD match'),'cad_status_mismatch');
    results.cad= cad;
    results.stage='activate_fake_devices_and_start';
    await activateAndStart();
    results.stage='capture_fake_camera_preview';
    await page.locator('[data-media-camera]').click();
    await page.waitForFunction(()=>document.querySelector('[data-media-video]').videoWidth>0);
    await page.locator('[data-media-capture]').click();
    await page.locator('[data-media-preview]').waitFor({state:'visible'});
    check(photos.length===0,'photo_uploaded_before_acceptance');
    results.stage='accept_fake_photo';
    await page.locator('[data-media-accept]').click();
    results.stage='observe_live_jitter_warning';
    // The observer retains actual DOM/state evidence after a transient notice clears.
    await page.waitForFunction(()=>window.__testJitterObserved);
    const warning=await page.evaluate(()=>window.__testJitterObserved);
    check(warning.whileActive&&warning.visible,'jitter_warning_not_visible');
    check(warning.captureBytes<=19200,'warning_capture_queue_limit');
    results.jitterWarningWhileActive=true;
    results.stage='await_native_completion';
    const completed=await until(()=>request('get','/media/state'),value=>value.phase==='completed'&&!value.active,'native_completion_timeout');
    check(completed.error===null&&completed.photoWarning===null,'native_completion_error_or_photo_fallback');
    await page.waitForFunction(()=>window.__testMedia?.state().actor===null);
    normalComplete=true;
    check(cancelCount===0,'normal_completion_cancelled');
    await collectCapture();
    results.stage='assert_primary_media_evidence';
    const observerResults=await Promise.all(responseTasks);
    check(observerResults.every(value=>value===null),'media_response_observer_failed');
    check(delayed&&delayMs>=590,'microphone_delay_not_observed');
    check(inputs.filter(value=>value.status===204&&value.acceptedBytes>0).length>=2,'insufficient_accepted_microphone_input');
    check(captureMaximum>0&&captureMaximum<=19200,'capture_queue_limit');
    const epochs=new Map();
    for (const input of inputs) {
      check(/^\d+$/.test(input.epoch||'')&&/^\d+$/.test(input.sequence||''),'input_headers_missing');
      const previous=epochs.get(input.epoch)??-1n;
      check(BigInt(input.sequence)===previous+1n,'submitted_input_sequence_gap');
      epochs.set(input.epoch,BigInt(input.sequence));
      check(input.status===204||input.status===409&&input.retired,'microphone_input_rejected');
    }
    check(pcm.length===5&&pcm.every(bytes=>bytes===4800),'fake_pcm_frames_mismatch');
    check(drains.length===5&&drains.every(value=>/^\d+$/.test(value.pcmBytes||''))
      &&BigInt(drains.at(-1).pcmBytes)===24000n,'fake_drain_evidence_mismatch');
    check(photos.length===1&&photos[0].status===204&&photos[0].acceptedBytes>12&&photos[0].acceptedBytes<=262144,'accepted_jpeg_evidence_mismatch');
    check(await page.locator('[data-media-error]').textContent()==='','media_error_visible');
    recordPrimaryMedia=false;
    Object.assign(results,{nativeCompleted:true,normalCompletionCancelCount:0,delayActualMs:delayMs,captureMaximumBytes:captureMaximum,
      microphoneInputs:inputs,photoAcceptedBytes:photos[0].acceptedBytes,photoFallback:false,pcmFrameBytes:pcm,
      drainCount:drains.length,cumulativeDrainedBytes:24000,pageErrorCount:pageErrors.length,
      physicalVerification:'unavailable'});
    for (const [name,width,height] of [['desktop',1400,900],['mobile',390,844]]) {
      results.stage=name+'_completed_layout';
      await page.setViewportSize({width,height});
      check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),name+'_horizontal_overflow');
      await page.screenshot({path:path.join(outputDir,name+'-completed.png')});
    }
    results.stage='completed_reload_no_replay';
    await page.reload();
    await selectSource();
    const reloaded=await request('get','');
    check(reloaded.snapshot.source.bootId===results.bootId&&startCount===1,'completion_reload_replayed_or_rebooted');
    results.reloadNoReplay=true;

    results.optionalActiveReload='not_run';
    if (process.env.KUFR_TEST_ACTIVE_RELOAD==='true') {
      // Optional regression: pending-photo pagehide cancellation and a new explicit Stop.
      results.optionalActiveReload='running';
      results.stage='prepare_active_photo_reload';
      await command('stop'); await command('inspect'); await idle();
      await activateAndStart();
      const reloadStarted=performance.now();
      results.stage='active_photo_reload_cancel';
      await page.reload();
      await until(()=>request('get','/media/state'),value=>!value.active,'active_reload_cancel_timeout',15000);
      results.activeReloadCancelMs=performance.now()-reloadStarted;
      check((await request('get','')).snapshot.source.bootId===results.bootId,'active_reload_rebooted');
      results.activeReloadCancelsMedia=true;
      await selectSource();
      results.stage='prepare_new_explicit_stop';
      await command('stop'); await command('inspect'); await idle();
      await activateAndStart();
      results.stage='new_conversation_explicit_stop';
      const stoppedResponse=page.waitForResponse(response=>new URL(response.url()).pathname===base+'/media/cancel');
      await page.locator('[data-media-stop]').click();
      check((await stoppedResponse).status()===204,'explicit_media_stop_failed');
      await until(()=>request('get','/media/state'),value=>!value.active,'explicit_stop_timeout',15000);
      results.newConversationAndExplicitStop=true;
      results.optionalActiveReload='passed';
    }
    check(pageErrors.length===0,'browser_page_errors');
    Object.assign(results,{nativeCompleted:true,normalCompletionCancelCount:0,delayActualMs:delayMs,captureMaximumBytes:captureMaximum,
      microphoneInputs:inputs,photoAcceptedBytes:photos[0].acceptedBytes,photoFallback:false,pcmFrameBytes:pcm,
      drainCount:drains.length,cumulativeDrainedBytes:24000,pageErrorCount:pageErrors.length,
      desktopViewport:[1400,900],mobileViewport:[390,844],physicalVerification:'unavailable'});
    results.stage='verification_complete';
  } catch(error) {
    primaryError={...metadata(error),stage:results.stage};
    if (page&&!page.isClosed()) await collectCapture().catch(()=>{});
  } finally {
    // Close the page first so pagehide cannot race restoration with live capture.
    if (page&&!page.isClosed()) {
      try { await page.close(); } catch(error) { cleanupErrors.push({stage:'page_close',...metadata(error)}); }
    }
    if (authenticated) {
      try {
        await command('stop');
        await command('inspect');
        await idle();
        if (original) {
          const current=await request('get','/settings');
          await request('put','/settings',{expectedRevision:current.revision,...writable(original)});
          check(sameSettings(await request('get','/settings'),original),'restored_settings_mismatch');
          results.settingsRestored=true;
        }
      } catch(error) { cleanupErrors.push({stage:'settings_restore',...metadata(error)}); }
      try { await command('release'); results.controlsReleased=true; }
      catch(error) { cleanupErrors.push({stage:'release',...metadata(error)}); }
    }
    if (browser) {
      try { await browser.close(); results.browserClosed=true; }
      catch(error) { cleanupErrors.push({stage:'browser_close',...metadata(error)}); }
    }
  }
  // Publish only after cleanup. Never serialize response bodies, settings, secrets, or media content.
  Object.assign(results,{passed:!primaryError&&cleanupErrors.length===0&&results.settingsRestored&&results.controlsReleased&&results.browserClosed,
    primaryError,cleanupErrors,normalCompleteObserved:normalComplete,delayActualMs:delayMs,captureMaximumBytes:captureMaximum,
    microphoneInputs:inputs,pcmFrameBytes:pcm,drains,photos,pageErrorCount:pageErrors.length,startCount,cancelCount});
  fs.writeFileSync(path.join(outputDir,'browser-media-result.json'),JSON.stringify(results,null,2));
  console.log(results.passed?'PASS isolated fake microphone jitter/native completion/CAD/reload; settings restored':'FAIL see sanitized browser-media-result.json');
  if (!results.passed) process.exitCode=1;
}
main().catch(error=>{console.error(JSON.stringify(metadata(error)));process.exitCode=1;});
