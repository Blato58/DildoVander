/* Real browser HAL over labeled native/media fixtures. No provider or hardware calls. */
const {test}=require('node:test');
const assert=require('node:assert/strict');
const http=require('node:http');
const fs=require('node:fs');
const path=require('node:path');
const media=require('./twin_media.js');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright');
const ROOT=path.resolve(__dirname,'../..');
const BOOT='00000000-0000-4000-8000-000000000001', SESSION='00000000-0000-4000-8000-000000000002';
test('real Chromium fake devices: delayed microphone recovery, JPEG, PCM drain and mobile layout',async()=>{
  const requests=[], outputs=[], errors=[]; let active=false,inputEnabled=false,drained=false,inputCount=0;
  const make=(kind,sequence,extra={})=>({sessionId:SESSION,bootId:BOOT,epoch:'9007199254740993',inputEpoch:'1',outputEpoch:'1',sequence,
    kind,byteLength:0,phase:kind,inputEnabled:['listening','pcm','drain'].includes(kind),drainedRequested:false,...extra});
  const complete=()=>{
    if(active && drained && inputCount>=2){active=false;inputEnabled=false;outputs.push(make('completed','5'));}
  };
  const settings={revision:'9007199254740993',engine:'fake',voice:'marin',speed:1,photoConsent:true,presentationVariant:'main',
    availability:{fake:true,realtime:false,gptLive:false,mic:true,speaker:true,camera:true}};
  const server=http.createServer(async(req,res)=>{
    const data=[];for await(const chunk of req)data.push(chunk);const body=Buffer.concat(data);
    requests.push({url:req.url,method:req.method,body,headers:req.headers});
    const json=value=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
    if(req.url==='/'){
      res.setHeader('Content-Type','text/html; charset=utf-8');res.end(`<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <link rel="stylesheet" href="/design-system/arcane-workshop.css"><style>${fs.readFileSync(path.join(__dirname,'viewer-workshop.css'),'utf8')}${fs.readFileSync(path.join(__dirname,'twin-viewer.css'),'utf8')}${fs.readFileSync(path.join(__dirname,'twin_media.css'),'utf8')}
        .twin-panel{top:8px;bottom:8px}.twin-panel>details{max-height:calc(100dvh - 36px)!important}</style>
        <body class="aw-root"><section class="twin-panel aw-root"><details open id="host"><summary>Browser HAL test fixture</summary></details></section>
        <script src="/twin_media_core.js"></script><script src="/twin_media_controller.js"></script><script src="/twin_media.js"></script><script type="module">
        import * as VapourUI from '/workshop-ui-runtime.js';
        window.VapourUI=VapourUI;
        window.hal=TwinMedia.mount(document,document.getElementById('host'),{request:async(id,route,options={})=>{
          const response=await fetch('/v1/twin/instances/'+encodeURIComponent(id)+route,options);
          if(!response.ok)throw new Error('Fixture HTTP '+response.status);return response.status===204?null:response.json();}});
        hal.sync({active:true,replay:false,role:'control',instanceId:'sim-integration',mode:'simulation',profile:'integration',bootId:'${BOOT}',status:'online'});
        </script></body></html>`);return;
    }
    if(req.url==='/workshop-ui-runtime.js'){res.setHeader('Content-Type','text/javascript');res.end(fs.readFileSync(path.join(__dirname,'workshop-ui-runtime.js')));return;}
    if(['/twin_media.js','/twin_media_core.js','/twin_media_controller.js'].includes(req.url)){res.setHeader('Content-Type','text/javascript');res.end(fs.readFileSync(path.join(__dirname,req.url.slice(1))));return;}
    if(req.url?.startsWith('/design-system/')){
      const file=path.resolve(ROOT,'.'+req.url);
      if(file.startsWith(path.join(ROOT,'design-system')+path.sep)&&fs.existsSync(file)){
        res.setHeader('Content-Type',file.endsWith('.css')?'text/css':'font/woff2');res.end(fs.readFileSync(file));return;
      }
    }
    if(req.url?.endsWith('/settings')){json(settings);return;}
    if(req.url?.endsWith('/media/start')){active=true;outputs.push(make('photo_request','1'));json({sessionId:SESSION,bootId:BOOT,epoch:'9007199254740993',inputEpoch:'0',outputEpoch:'0',phase:'starting',active:true});return;}
    if(req.url?.endsWith('/media/output')){if(outputs.length)json(outputs.shift());else{res.statusCode=204;res.end();}return;}
    if(req.url?.endsWith('/media/state')){json({sessionId:SESSION,bootId:BOOT,epoch:'9007199254740993',inputEpoch:'1',outputEpoch:'1',phase:'fixture',active,inputEnabled});return;}
    if(req.url?.endsWith('/media/photo')){
      if(!body.length){res.statusCode=204;res.end();return;}
      assert.ok(body.length>12&&body.length<=262144);assert.equal(body.readUInt16BE(0),0xffd8);
      assert.deepEqual(Buffer.from(media.stripJpegMetadata(new Uint8Array(body))),body,'Browser photo must already have application metadata stripped');
      if(process.env.TWIN_JPEG_DIAGNOSTIC_DIR){
        const fixture=path.join(process.env.TWIN_JPEG_DIAGNOSTIC_DIR,'chrome-camera-accepted.jpg');fs.writeFileSync(fixture,body);
        console.log('JPEG fixture',JSON.stringify({fixture,bytes:body.length}));
      }
      inputEnabled=true;
      outputs.push(make('listening','2'),make('pcm','3',{byteLength:14400,pcmBase64:Buffer.alloc(14400).toString('base64')}),make('drain','4',{pcmBytes:'14400',drainedRequested:true}));
      res.statusCode=204;res.end();return;
    }
    if(req.url?.endsWith('/media/played')){
      const value=JSON.parse(body);if(value.drained){drained=true;complete();}res.statusCode=204;res.end();return;
    }
    if(req.url?.endsWith('/media/input')){
      inputCount++;assert.equal(req.headers['input-sequence'],String(inputCount-1));
      if(inputCount===1)await new Promise(resolve=>setTimeout(resolve,600));
      complete();res.statusCode=204;res.end();return;
    }
    if(req.url?.endsWith('/media/cancel')){res.statusCode=204;res.end();return;}
    if(req.url==='/favicon.ico'){res.statusCode=204;res.end();return;}
    res.statusCode=404;res.end();
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  let browser,page;
  try{
    browser=await chromium.launch({channel:process.env.PLAYWRIGHT_CHANNEL,headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream']});
    page=await browser.newPage({viewport:{width:390,height:844}});page.on('pageerror',error=>errors.push(error.message));
    page.on('console',message=>{if(message.type()==='error')errors.push(message.text());});
    const base='http://127.0.0.1:'+server.address().port;await page.goto(base);
    await page.locator('[data-media-revision]').filter({hasText:'Revision 9007199254740993'}).waitFor({state:'attached'});
    assert.equal(requests.some(item=>item.url.endsWith('/media/start')),false);
    assert.equal(await page.locator('[data-media-photo]').isVisible(),false);
    assert.equal(await page.locator('[data-media-preview]').isVisible(),false);
    await page.getByRole('button',{name:'Aktivovat reproduktor',exact:true}).click();
    await page.getByRole('button',{name:'Aktivovat mikrofon',exact:true}).click();
    await page.waitForFunction(()=>hal.state().mic&&hal.state().speaker);
    await page.getByRole('button',{name:'Spustit rozhovor',exact:true}).click();
    await page.getByRole('button',{name:'Aktivovat kameru',exact:true}).waitFor({state:'visible'});
    assert.equal(requests.filter(item=>item.url.endsWith('/media/photo')).length,0);
    await page.getByRole('button',{name:'Aktivovat kameru',exact:true}).click();
    await page.waitForFunction(()=>hal.state().camera||document.querySelector('[data-media-error]').textContent.length>0);
    assert.equal(await page.evaluate(()=>hal.state().camera),true,await page.locator('[data-media-error]').textContent());
    await page.waitForFunction(()=>document.querySelector('[data-media-video]').videoWidth>0,undefined,{timeout:5000});
    assert.equal(await page.locator('[data-media-video]').isVisible(),true);
    await page.getByRole('button',{name:'Pořídit náhled',exact:true}).click();
    await page.locator('[data-media-preview]').waitFor({state:'visible'});
    assert.equal(requests.filter(item=>item.url.endsWith('/media/photo')).length,0);
    await page.getByRole('button',{name:'Přijmout a předat jednu fotku',exact:true}).click();
    await page.locator('[data-media-status]').filter({hasText:'část neodeslaného zvuku byla přeskočena'}).waitFor({state:'visible'});
    const recovering=await page.evaluate(()=>hal.state());assert.ok(recovering.actor);assert.ok(recovering.captureBytes<=media.CAPTURE_BYTES);
    assert.equal(await page.locator('[data-media-error]').textContent(),'');
    await page.waitForFunction(()=>hal.state().actor===null);
    const played=requests.filter(item=>item.url.endsWith('/media/played')).map(item=>JSON.parse(item.body));
    assert.equal(played.at(-1).pcmBytes,'14400');assert.equal(played.at(-1).drained,true);
    const inputs=requests.filter(item=>item.url.endsWith('/media/input'));
    assert.ok(inputs.length>=2);assert.ok(inputs.every(item=>item.body.length===9600));
    assert.equal(requests.filter(item=>item.url.endsWith('/media/photo')).length,1);
    assert.equal(requests.filter(item=>item.url.endsWith('/media/cancel')).length,0);
    const start=JSON.parse(requests.find(item=>item.url.endsWith('/media/start')).body);
    assert.equal(start.micAvailable,true);assert.equal(start.speakerAvailable,true);assert.equal(start.cameraAvailable,true);
    for(const viewport of [{width:390,height:844},{width:320,height:800},{width:1280,height:900}]){
      await page.setViewportSize(viewport);assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
    }
    assert.deepEqual(errors,[]);assert.ok(requests.every(item=>!item.url.includes('openai')));
    console.log('PASS fake Chromium microphone recovers from600ms upload, bounded capture and sequential input; speaker/camera JPEG/PCM drain, 390/320/1280px; no provider requests');
  }catch(error){
    if(page&&!page.isClosed())console.log('HAL diagnostic',await page.evaluate(()=>({state:hal.state(),status:document.querySelector('[data-media-status]').textContent,error:document.querySelector('[data-media-error]').textContent})),errors);
    throw error;
  }finally{await browser?.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
});

test('Chrome navigation preserves scoped unload cancellation through a delayed response',async()=>{
  const requests=[],errors=[];let receiveCancel,finishCancel;
  const cancelled=new Promise(resolve=>{receiveCancel=resolve;}),responded=new Promise(resolve=>{finishCancel=resolve;});
  const actor={sessionId:SESSION,bootId:BOOT,epoch:'18446744073709551615',inputEpoch:'0',outputEpoch:'0',phase:'listening',active:true,inputEnabled:false};
  const settings={revision:'1',engine:'fake',voice:'marin',speed:1,photoConsent:false,presentationVariant:'main',
    availability:{fake:true,realtime:false,gptLive:false,mic:true,speaker:true,camera:true}};
  const server=http.createServer(async(req,res)=>{
    const chunks=[];for await(const chunk of req)chunks.push(chunk);const body=Buffer.concat(chunks);
    const entry={url:req.url,method:req.method,body};requests.push(entry);
    const json=value=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
    if(req.url==='/'){
      res.setHeader('Content-Type','text/html; charset=utf-8');res.end(`<!doctype html><html><body><div id="host"></div>
        <script src="/twin_media_core.js"></script><script src="/twin_media_controller.js"></script><script src="/twin_media.js"></script><script type="module">
        import * as VapourUI from '/workshop-ui-runtime.js';
        window.VapourUI=VapourUI;
        window.hal=TwinMedia.mount(document,document.getElementById('host'),{request:async(id,route,options={})=>{
          const response=await fetch('/v1/twin/instances/'+encodeURIComponent(id)+route,options);
          if(!response.ok)throw new Error('Fixture HTTP '+response.status);return response.status===204?null:response.json();}});
        hal.sync({active:true,replay:false,role:'control',instanceId:'sim-unload',mode:'simulation',profile:'integration',bootId:'${BOOT}',status:'online'});
        </script></body></html>`);return;
    }
    if(req.url==='/exit'){res.setHeader('Content-Type','text/html');res.end('<!doctype html><title>Departed media page</title>');return;}
    if(req.url==='/workshop-ui-runtime.js'){res.setHeader('Content-Type','text/javascript');res.end(fs.readFileSync(path.join(__dirname,'workshop-ui-runtime.js')));return;}
    if(['/twin_media.js','/twin_media_core.js','/twin_media_controller.js'].includes(req.url)){res.setHeader('Content-Type','text/javascript');res.end(fs.readFileSync(path.join(__dirname,req.url.slice(1))));return;}
    if(req.url?.endsWith('/settings')){json(settings);return;}
    if(req.url?.endsWith('/media/start')||req.url?.endsWith('/media/state')){json(actor);return;}
    if(req.url?.endsWith('/media/cancel')){
      receiveCancel(entry);
      // Finish after navigation; the departed document cannot await this.
      await new Promise(resolve=>setTimeout(resolve,300));const responseAborted=res.destroyed;
      res.statusCode=204;res.end();finishCancel(responseAborted);return;
    }
    res.statusCode=204;res.end();
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));let browser,timeout;
  try{
    browser=await chromium.launch({channel:process.env.PLAYWRIGHT_CHANNEL,headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream']});
    const page=await browser.newPage();page.on('pageerror',error=>errors.push(error.message));
    const base='http://127.0.0.1:'+server.address().port;await page.goto(base);
    await page.waitForFunction(()=>document.querySelector('[data-media-revision]').textContent.includes('Revision 1'));
    await page.getByRole('button',{name:'Aktivovat reproduktor',exact:true}).click();
    await page.getByRole('button',{name:'Aktivovat mikrofon',exact:true}).click();
    await page.waitForFunction(()=>hal.state().mic&&hal.state().speaker);
    await page.getByRole('button',{name:'Spustit rozhovor',exact:true}).click();await page.waitForFunction(()=>hal.state().actor!==null);
    await page.goto(base+'/exit');
    const entry=await Promise.race([cancelled,new Promise((_,reject)=>{timeout=setTimeout(()=>reject(new Error('Unload cancellation was not received')),5000);})]);
    clearTimeout(timeout);assert.equal(await responded,false,'Keepalive request must survive until the delayed response');
    await page.close();
    assert.equal(entry.method,'POST');assert.equal(entry.url,'/v1/twin/instances/sim-unload/media/cancel');
    assert.deepEqual(JSON.parse(entry.body),{sessionId:SESSION,bootId:BOOT,epoch:'18446744073709551615'});assert.ok(entry.body.length<256);
    assert.equal(requests.filter(item=>item.url.endsWith('/media/cancel')).length,1);
    assert.equal(requests.some(item=>item.url.endsWith('/commands')||item.url.endsWith('/media/input')||item.url.endsWith('/media/photo')),false);
    assert.deepEqual(errors,[]);
  }finally{clearTimeout(timeout);await browser?.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
});
