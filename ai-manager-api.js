const crypto=require('crypto'),fs=require('fs'),path=require('path');
const PROTOCOL_VERSION=1;
const ACTIONS=new Set(['send_lobby_announcement','send_lobby_popup','send_player_message','get_status_snapshot','start_event','stop_event']);

function createAiManagerApi(o={}){
  const dataDir=o.dataDir||process.env.DATA_DIR||'/tmp';
  const serverId=o.serverId||process.env.AI_BRIDGE_SERVER_ID||'5heads-arena-main';
  const secret=o.bridgeSecret||process.env.AI_BRIDGE_SECRET||'';
  const adminToken=o.adminToken||process.env.AI_ADMIN_TOKEN||'';
  const skew=nint(o.maxClockSkewSeconds||process.env.AI_BRIDGE_CLOCK_SKEW_SECONDS,10,300,60);
  const onEvent=typeof o.onEvent==='function'?o.onEvent:null;
  fs.mkdirSync(dataDir,{recursive:true});
  const f={
    heartbeat:path.join(dataDir,'ai_bridge_heartbeat.json'),
    heartbeats:path.join(dataDir,'ai_bridge_heartbeats.ndjson'),
    events:path.join(dataDir,'ai_bridge_events.ndjson'),
    commands:path.join(dataDir,'ai_bridge_commands.json'),
    results:path.join(dataDir,'ai_bridge_results.ndjson'),
    audit:path.join(dataDir,'ai_bridge_audit.ndjson')
  };
  let commands=readJson(f.commands,[]); if(!Array.isArray(commands))commands=[];
  const nonces=new Map();
  const seenEventIds=new Set(tail(f.events,2000).map(x=>x&&x.event_id).filter(Boolean));

  function rememberEventId(eventId){
    if(!eventId)return;
    seenEventIds.add(eventId);
    if(seenEventIds.size>4000){
      const keep=Array.from(seenEventIds).slice(-2000);
      seenEventIds.clear();
      for(const id of keep)seenEventIds.add(id);
    }
  }
  function dispatchAcceptedEvent(event){
    if(!onEvent)return;
    Promise.resolve(onEvent(event,{
      getLatestHeartbeat:()=>readJson(f.heartbeat,null),
      queueInternalCommand
    })).catch(e=>console.error('[AI API] Event handler failed:',e.message));
  }

  function audit(type,extra={}){append(f.audit,{audit_id:'audit_'+id(),at:new Date().toISOString(),type,server_id:serverId,...extra});}
  function saveCommands(){atomic(f.commands,commands);}
  function configured(){return secret.length>=32&&adminToken.length>=24;}
  function pruneNonces(now){for(const [n,t] of nonces)if(t<now-skew-5)nonces.delete(n);}

  function verify(req,raw){
    if(secret.length<32)return err(503,'BRIDGE_NOT_CONFIGURED','AI_BRIDGE_SECRET is not configured.');
    const sid=String(req.headers['x-5h-server-id']||''),ts=String(req.headers['x-5h-timestamp']||'');
    const nonce=String(req.headers['x-5h-nonce']||''),hash=String(req.headers['x-5h-body-sha256']||'').toLowerCase();
    const sig=String(req.headers['x-5h-signature']||'').toLowerCase(),proto=String(req.headers['x-5h-protocol-version']||'');
    if(sid!==serverId)return err(401,'AUTH_FAILED','Unknown server ID.');
    if(proto!=='1')return err(400,'PROTOCOL_MISMATCH','Unsupported protocol version.');
    const now=Math.floor(Date.now()/1000),t=Number(ts);
    if(!Number.isFinite(t)||Math.abs(now-t)>skew)return err(401,'CLOCK_SKEW','Timestamp outside accepted window.');
    if(!/^[A-Za-z0-9-]{16,128}$/.test(nonce))return err(401,'AUTH_FAILED','Invalid nonce.');
    pruneNonces(now); if(nonces.has(nonce))return err(409,'REPLAY_DETECTED','Nonce already used.');
    const actual=crypto.createHash('sha256').update(raw||'').digest('hex');
    if(!safeHex(actual,hash))return err(401,'AUTH_FAILED','Body hash mismatch.');
    const canonical=[String(req.method||'GET').toUpperCase(),req.url||'/',ts,nonce,actual].join('\n');
    const expected=crypto.createHmac('sha256',secret).update(canonical).digest('hex');
    if(!safeHex(expected,sig))return err(401,'AUTH_FAILED','Invalid signature.');
    nonces.set(nonce,now); return {ok:true};
  }

  function verifyAdmin(req){
    const a=String(req.headers.authorization||''); if(!a.startsWith('Bearer ')||adminToken.length<24)return false;
    const x=Buffer.from(a.slice(7)),y=Buffer.from(adminToken); return x.length===y.length&&crypto.timingSafeEqual(x,y);
  }

  async function handle(req,res){
    try{
      const u=new URL(req.url||'/','http://localhost');
      if(u.pathname==='/ai-health')return json(res,200,{status:configured()?'ok':'not_configured',protocol_version:1,server_id:serverId,bridge_configured:secret.length>=32,admin_api_configured:adminToken.length>=24,last_heartbeat:readJson(f.heartbeat,null)?.received_at||null,pending_commands:commands.filter(x=>['queued','delivered'].includes(x.status)).length});
      if(u.pathname.startsWith('/v1/admin/'))return handleAdmin(req,res,u);
      const m=u.pathname.match(/^\/v1\/servers\/([^/]+)\/(heartbeat|events|commands|command-results)$/); if(!m)return false;
      if(decodeURIComponent(m[1])!==serverId)return sendErr(res,404,'SERVER_NOT_FOUND','Unknown server.');
      const raw=await rawBody(req,1024*1024),v=verify(req,raw); if(!v.ok){audit('bridge_auth_rejected',{code:v.code,path:u.pathname});return sendErr(res,v.status,v.code,v.message);}
      if(m[2]==='heartbeat')return heartbeat(req,res,raw);
      if(m[2]==='events')return events(req,res,raw);
      if(m[2]==='commands')return poll(req,res,u);
      return result(req,res,raw);
    }catch(e){console.error('[AI API]',e);return sendErr(res,500,'INTERNAL_ERROR','AI Manager API request failed.');}
  }

  function heartbeat(req,res,raw){
    if(req.method!=='POST')return method(res,'POST'); const b=parse(raw,res); if(!b)return true;
    if(b.protocol_version!==1||b.server_id!==serverId||!b.instance_id||!b.server||!b.health)return sendErr(res,400,'INVALID_REQUEST','Invalid heartbeat.');
    const record={received_at:new Date().toISOString(),...b}; atomic(f.heartbeat,record); append(f.heartbeats,record); trim(f.heartbeats,8e6,5000); return json(res,200,ok());
  }
  function events(req,res,raw){
    if(req.method!=='POST')return method(res,'POST'); const b=parse(raw,res); if(!b)return true;
    if(b.protocol_version!==1||!Array.isArray(b.events)||b.events.length>20)return sendErr(res,400,'INVALID_REQUEST','Invalid event batch.');
    let accepted=0;
    for(const e of b.events){
      if(!e||e.protocol_version!==1||e.server_id!==serverId||typeof e.event_id!=='string'||typeof e.event_type!=='string')continue;
      if(seenEventIds.has(e.event_id))continue;
      append(f.events,{received_at:new Date().toISOString(),...e});
      rememberEventId(e.event_id);
      accepted++;
      dispatchAcceptedEvent(e);
    }
    trim(f.events,16e6,10000); return json(res,200,{...ok(),accepted});
  }
  function expire(){
    let changed=false; for(const c of commands)if(c&&['queued','delivered'].includes(c.status)&&Date.parse(c.expires_at)<=Date.now()){c.status='expired';c.completed_at=new Date().toISOString();audit('command_expired',{command_id:c.command_id,action:c.action});changed=true;} if(changed)saveCommands();
  }
  function poll(req,res,u){
    if(req.method!=='GET')return method(res,'GET'); expire(); const limit=nint(u.searchParams.get('limit'),1,50,10),now=Date.now();
    const out=commands.filter(c=>c&&['queued','delivered'].includes(c.status)&&Date.parse(c.expires_at)>now).slice(0,limit);
    if(out.length){const at=new Date().toISOString();for(const c of out){c.status='delivered';c.last_delivered_at=at;c.delivery_count=(c.delivery_count||0)+1;}saveCommands();}
    return json(res,200,{protocol_version:1,commands:out.map(publicCommand)});
  }
  function result(req,res,raw){
    if(req.method!=='POST')return method(res,'POST'); const r=parse(raw,res); if(!r)return true;
    if(r.protocol_version!==1||r.server_id!==serverId||typeof r.command_id!=='string'||typeof r.status!=='string')return sendErr(res,400,'INVALID_REQUEST','Invalid command result.');
    append(f.results,{received_at:new Date().toISOString(),...r});trim(f.results,8e6,5000);
    const c=commands.find(x=>x.command_id===r.command_id); if(c){c.status=['succeeded','failed','rejected','expired','duplicate'].includes(r.status)?r.status:'failed';c.completed_at=r.finished_at||new Date().toISOString();c.result=r.result||null;c.error=r.error||null;saveCommands();audit('command_completed',{command_id:c.command_id,action:c.action,status:c.status,requested_by:c.requested_by,reason:c.reason,error:r.error||null});}
    else audit('orphan_command_result',{command_id:r.command_id,status:r.status});
    return json(res,200,ok());
  }

  async function handleAdmin(req,res,u){
    if(!verifyAdmin(req))return sendErr(res,401,'ADMIN_AUTH_FAILED','Invalid admin bearer token.');
    if(u.pathname==='/v1/admin/state'&&req.method==='GET'){expire();return json(res,200,{ok:true,server_id:serverId,heartbeat:readJson(f.heartbeat,null),commands:commands.slice(-50).reverse()});}
    if(u.pathname==='/v1/admin/audit'&&req.method==='GET')return json(res,200,{ok:true,server_id:serverId,audit:tail(f.audit,nint(u.searchParams.get('limit'),1,500,100))});
    if(u.pathname==='/v1/admin/events'&&req.method==='GET')return json(res,200,{ok:true,server_id:serverId,events:tail(f.events,nint(u.searchParams.get('limit'),1,500,100))});
    if(u.pathname==='/v1/admin/commands'&&req.method==='POST'){const b=parse(await rawBody(req,256*1024),res);if(!b)return true;return enqueue(res,b);}
    return sendErr(res,404,'NOT_FOUND','Admin route not found.');
  }

  function enqueue(res,b){
    if((b.server_id||serverId)!==serverId)return sendErr(res,400,'SERVER_NOT_FOUND','Unknown server.');
    const action=typeof b.action==='string'?b.action.trim():''; if(!ACTIONS.has(action))return sendErr(res,400,'ACTION_NOT_ALLOWED','Action is not in the Railway Phase 1 allowlist.');
    const args=b.arguments&&typeof b.arguments==='object'&&!Array.isArray(b.arguments)?b.arguments:{},v=validate(action,args); if(!v.ok)return sendErr(res,400,v.code||'INVALID_ARGUMENTS',v.message);
    const now=new Date(),c={protocol_version:1,command_id:'cmd_'+id(),server_id:serverId,action,issued_at:now.toISOString(),expires_at:new Date(now.getTime()+nint(b.expires_in_seconds,10,600,60)*1000).toISOString(),requested_by:requestedBy(b.requested_by),reason:text(b.reason||'Admin API request',300),arguments:args,status:'queued',created_at:now.toISOString(),last_delivered_at:null,delivery_count:0,completed_at:null,result:null,error:null};
    commands.push(c);if(commands.length>2000)commands=commands.slice(-2000);saveCommands();audit('command_queued',{command_id:c.command_id,action:c.action,requested_by:c.requested_by,reason:c.reason});return json(res,201,{ok:true,command:publicCommand(c)});
  }

  function queueInternalCommand(action,args,meta={}){
    return new Promise((resolve,reject)=>{const r=capture((status,payload)=>status<300?resolve(payload.command):reject(new Error(payload?.error?.message||'Rejected')));enqueue(r,{action,arguments:args||{},reason:meta.reason||'Internal request',requested_by:meta.requested_by||{type:'railway',id:'internal'},expires_in_seconds:meta.expires_in_seconds||60});});
  }

  return {handle,isConfigured:configured,configStatus:()=>({serverId,bridgeSecretConfigured:secret.length>=32,adminTokenConfigured:adminToken.length>=24}),getLatestHeartbeat:()=>readJson(f.heartbeat,null),queueInternalCommand,files:f};
}

function validate(a,x){
  if(a==='send_lobby_announcement')return validText(x.message,'message',1,180);
  if(a==='send_lobby_popup'){let v=validText(x.title,'title',1,60);if(!v.ok)return v;v=validText(x.message,'message',1,100);if(!v.ok)return v;if(x.subtext!=null&&(v=validText(x.subtext,'subtext',0,160),!v.ok))return v;const d=x.duration_seconds==null?10:Number(x.duration_seconds);return Number.isFinite(d)&&d>=4&&d<=20?{ok:true}:{ok:false,code:'OUT_OF_RANGE',message:'duration_seconds must be 4-20.'};}
  if(a==='send_player_message'){if(!/^\d{17,24}$/.test(String(x.steam_id||'')))return {ok:false,message:'steam_id must be numeric.'};return validText(x.message,'message',1,180);}
  if(a==='get_status_snapshot')return {ok:true};
  if(a==='start_event'){let v=validText(x.event_id,'event_id',1,64);if(!v.ok)return v;const d=Number(x.duration_minutes==null?30:x.duration_minutes);return Number.isInteger(d)&&d>=1&&d<=240?{ok:true}:{ok:false,code:'OUT_OF_RANGE',message:'duration_minutes must be 1-240.'};}
  if(a==='stop_event')return validText(x.event_id,'event_id',1,64);return {ok:false,code:'ACTION_NOT_ALLOWED',message:'Unsupported action.'};
}
function validText(v,k,min,max){if(typeof v!=='string'||v.trim().length<min||v.trim().length>max||/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/.test(v))return {ok:false,code:'INVALID_ARGUMENTS',message:`${k} must be ${min}-${max} safe characters.`};return {ok:true};}
function publicCommand(c){return {protocol_version:1,command_id:c.command_id,action:c.action,issued_at:c.issued_at,expires_at:c.expires_at,requested_by:c.requested_by,reason:c.reason,arguments:c.arguments};}
function requestedBy(v){return !v||typeof v!=='object'?{type:'admin',id:'api'}:{type:text(v.type||'admin',32),id:text(v.id||'api',128)};}
function text(v,n){const s=String(v??'').replace(/[\r\n]+/g,' ').trim();return s.length<=n?s:s.slice(0,n);}
function id(){return crypto.randomUUID().replace(/-/g,'');}
function err(status,code,message){return {ok:false,status,code,message};}
function safeHex(a,b){if(typeof a!=='string'||typeof b!=='string'||a.length!==b.length||!/^[0-9a-f]+$/i.test(a+b))return false;try{return crypto.timingSafeEqual(Buffer.from(a,'hex'),Buffer.from(b,'hex'));}catch{return false;}}
function ok(){return {ok:true,request_id:'req_'+id(),server_time:new Date().toISOString()};}
function json(res,status,body){if(res.writableEnded)return true;res.statusCode=status;res.setHeader('Content-Type','application/json; charset=utf-8');res.setHeader('Cache-Control','no-store');res.end(JSON.stringify(body));return true;}
function sendErr(res,status,code,message){return json(res,status,{ok:false,request_id:'req_'+id(),error:{code,message}});}
function method(res,...allow){res.setHeader('Allow',allow.join(', '));return sendErr(res,405,'METHOD_NOT_ALLOWED','Method not allowed.');}
function parse(raw,res){try{return raw?JSON.parse(raw):{};}catch{sendErr(res,400,'INVALID_JSON','Body is not valid JSON.');return null;}}
function rawBody(req,max){if(req.method==='GET'||req.method==='HEAD')return Promise.resolve('');return new Promise((resolve,reject)=>{const c=[];let n=0;req.on('data',b=>{n+=b.length;if(n>max){reject(new Error('Body too large'));req.destroy();return;}c.push(b);});req.on('end',()=>resolve(Buffer.concat(c).toString('utf8')));req.on('error',reject);});}
function readJson(file,fallback){try{return fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):fallback;}catch{return fallback;}}
function atomic(file,v){const t=file+'.tmp';fs.writeFileSync(t,JSON.stringify(v,null,2));fs.renameSync(t,file);}
function append(file,v){fs.appendFileSync(file,JSON.stringify(v)+'\n');}
function tail(file,n){try{if(!fs.existsSync(file))return[];return fs.readFileSync(file,'utf8').trim().split('\n').filter(Boolean).slice(-n).reverse().map(x=>{try{return JSON.parse(x);}catch{return null;}}).filter(Boolean);}catch{return[];}}
function trim(file,max,keep){try{if(!fs.existsSync(file)||fs.statSync(file).size<=max)return;const l=fs.readFileSync(file,'utf8').trim().split('\n');fs.writeFileSync(file,l.slice(-keep).join('\n')+'\n');}catch(e){console.warn('[AI API] trim failed:',e.message);}}
function nint(v,min,max,fallback){const x=parseInt(v,10);return Number.isFinite(x)?Math.max(min,Math.min(max,x)):fallback;}
function capture(done){return {statusCode:200,writableEnded:false,headersSent:false,setHeader(){},end(p){this.writableEnded=true;let b={};try{b=JSON.parse(p||'{}')}catch{}done(this.statusCode,b);}};}

module.exports={createAiManagerApi,COMPILED_ACTIONS:ACTIONS,PROTOCOL_VERSION};
