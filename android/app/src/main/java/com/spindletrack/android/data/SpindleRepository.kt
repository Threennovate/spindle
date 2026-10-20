package com.spindletrack.android.data

import android.content.Context
import androidx.work.*
import androidx.room.withTransaction
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.*
import okhttp3.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.io.IOException
import java.time.*
import java.util.UUID
import java.util.concurrent.TimeUnit

@Serializable data class EntryDraft(val description:String="",val projectId:String?=null,val taskId:String?=null,val tagIds:List<String> = emptyList(),val isBillable:Boolean=false,val shares:List<TimeShareRequest>?=null,val describeParts:Boolean?=null,val start:String=Instant.now().toString(),val stop:String?=null)

/**
 * The parts as the server will keep them after [draft] is saved over [entry]: with their own descriptions when those are
 * saved, or, when the draft doesn't say (changes made outside the editor), kept by project and task while the entry's
 * description stays as it was.
 */
fun shownShares(draft: EntryDraft, entry: TimeEntryResponse?): List<TimeShareResponse> {
    val shares = draft.shares ?: return entry?.shares.orEmpty().takeIf { draft.projectId == entry?.projectId }.orEmpty()
    val split = shares.size > 1
    val kept = entry?.takeIf { draft.describeParts == null && it.shares.size > 1 && it.shares.any { share -> share.description != null } && draft.description.trim() == it.description }?.shares
    return shares.map { part ->
        val description = when {
            !split -> null
            draft.describeParts == true -> part.description ?: ""
            draft.describeParts == false || kept == null -> null
            else -> kept.firstOrNull { it.projectId == part.projectId && it.taskId == part.taskId }?.description ?: ""
        }
        TimeShareResponse(part.projectId, part.taskId, part.isBillable ?: false, part.percent, description)
    }
}

data class ExportFile(val file:File,val mimeType:String,val fileName:String)
data class AppState(val me:MeResponse?=null,val workspaceId:String?=null,val projects:List<ProjectResponse> = emptyList(),val tags:List<TagResponse> = emptyList(),val clients:List<ClientResponse> = emptyList(),val settings:WorkspaceSettingsResponse?=null,val entries:List<TimeEntryResponse> = emptyList(),val running:TimeEntryResponse?=null,val pendingCount:Int=0,val offline:Boolean=false,val loading:Boolean=false,val error:String?=null,val failedChanges:List<PendingChange> = emptyList(),val serverInfo:ServerResponse?=null,val sessionExpired:Boolean=false)
class ApiException(val status:Int,override val message:String,val current:TimeEntryResponse?=null):Exception(message){
 /** The change was refused because the organization is read-only: its trial or plan has ended. */
 val isReadOnly:Boolean get()=status==402
 /** The entry changed elsewhere since this device saw it, so the change wasn't saved; [current] is how it is now. */
 val isEntryChanged:Boolean get()=status==412 && current!=null
}
const val EntryChangedMessage="This entry changed on another device. Retry to keep your version, or discard yours to keep theirs."
const val ReadOnlyMessage="Nothing can be changed here: the trial or plan has ended."
@Serializable private data class Session(val server:String,val account:String,val tokens:AuthTokensResponse)
@Serializable private data class CachedState(val me:MeResponse,val workspaceId:String?,val projects:List<ProjectResponse>,val tags:List<TagResponse>,val clients:List<ClientResponse>,val settings:WorkspaceSettingsResponse?,val entries:List<TimeEntryResponse>,val running:TimeEntryResponse?)

class SpindleRepository(context:Context,sessionStore:SessionStore=SecureSession(context),private val scheduleSync:(()->Unit)?=null){
 private val context=context.applicationContext
 private val prefs=context.getSharedPreferences("configuration",Context.MODE_PRIVATE)
 private val vault=sessionStore
 private val db=SpindleDatabase.get(context)
 private val dao=db.dao()
 val json=Json{ignoreUnknownKeys=true;encodeDefaults=true}
 private var session:Session?=null
 private var initialized=false
 @Volatile private var foreground=false
 private val liveLock=Any()
 private val initialization=Mutex()
 private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO)
 private val mutable=MutableStateFlow(AppState())
 val state:StateFlow<AppState> = mutable.asStateFlow()
 val serverUrl:String get()=prefs.getString("server",com.spindletrack.android.BuildConfig.SERVER_URL)!!
 private val http=OkHttpClient.Builder().connectTimeout(20,TimeUnit.SECONDS).readTimeout(30,TimeUnit.SECONDS).build()
 @Volatile private var hub:HubConnection?=null
 private var syncJob:Job?=null
 private var from=LocalDate.now().minusDays(30)
 private var to=LocalDate.now().plusDays(1)
 private var baseEntries:List<TimeEntryResponse> = emptyList()
 private var baseRunning:TimeEntryResponse?=null
 private fun account():String=session?.let{it.server+"|"+it.account}?:error("Sign in first.")
 private fun workspace():String=state.value.workspaceId?:error("Choose a workspace first.")
 private fun path(workspace:String=workspace())="/api/workspaces/$workspace"
 private fun update(transform:(AppState)->AppState){mutable.update(transform)}

 suspend fun configureServer(url:String){
  val normalized=url.trim().trimEnd('/')
  val parsed=normalized.toHttpUrlOrNull()?:error("Enter a valid server URL.")
  require(parsed.isHttps || parsed.host in listOf("localhost","127.0.0.1","10.0.2.2")){"Use HTTPS for your server."}
  if(normalized!=serverUrl){signOut();prefs.edit().putString("server",normalized).commit();update{it.copy(serverInfo=null)};loadServerInfo()}
 }
 suspend fun initialize()=initialization.withLock{
  if(initialized)return@withLock
  initialized=true
  loadServerInfo()
  session=vault.read()?.let{runCatching{json.decodeFromString<Session>(it)}.getOrNull()}
  if(session?.server!=serverUrl){session=null;vault.clear()}
  if(session==null)return@withLock
  operations.withLock{restoreCache();overlay()}
  refresh()
  if(foreground)startLiveSync()
 }
 suspend fun signIn(email:String,password:String){authenticate("login",buildJsonObject{put("email",email);put("password",password);put("deviceName",android.os.Build.MODEL)})}
 suspend fun register(fullName:String,email:String,password:String,acceptTerms:Boolean=false){authenticate("register",buildJsonObject{put("fullName",fullName);put("email",email);put("password",password);put("timeZone",ZoneId.systemDefault().id);put("deviceName",android.os.Build.MODEL);put("acceptTerms",acceptTerms)})}
 /**
  * The address that starts signing in with Google or Apple in the browser. The verifier is kept with the server it
  * belongs to, so the sign-in can finish even when Android ends the app while the browser is open.
  */
 fun startExternalSignIn(provider:String,invitation:String?=null):String{
  val verifier=Pkce.verifier()
  prefs.edit().putString("externalVerifier",verifier).putString("externalProvider",provider).putString("externalServer",serverUrl).commit()
  return externalSignInUrl(serverUrl,provider,Pkce.challenge(verifier),ZoneId.systemDefault().id,invitation)
 }
 fun forgetExternalSignIn(){prefs.edit().remove("externalVerifier").remove("externalProvider").remove("externalServer").apply()}
 /** Trades the browser's one-time code for a session, as a password sign-in does. A code works once, so the verifier goes either way. */
 suspend fun finishExternalSignIn(code:String){
  val verifier=prefs.getString("externalVerifier",null)?.takeIf{prefs.getString("externalServer",null)==serverUrl}
  forgetExternalSignIn()
  authenticate("external/exchange",buildJsonObject{put("code",code);put("codeVerifier",verifier?:error("This sign-in has expired. Please try again."));put("deviceName",android.os.Build.MODEL)})
 }
 private suspend fun authenticate(endpoint:String,body:JsonObject){
  // Google and Apple only say whose account it is afterwards; that is checked below.
  check(session==null || endpoint=="external/exchange" || (endpoint=="login" && body["email"]?.jsonPrimitive?.content?.equals(state.value.me?.email,ignoreCase=true)==true)){"Sign out before switching accounts."}
  update{it.copy(loading=true,error=null)}
  try{
   val tokens=json.decodeFromJsonElement<AuthTokensResponse>(raw("POST","/api/auth/$endpoint",body,false))
   val me=json.decodeFromJsonElement<MeResponse>(raw("GET","/api/me",token=tokens.accessToken))
   // Signing in again after the session ended keeps this phone's unsent changes, so it has to be the same account.
   session?.takeIf{it.account!=me.id}?.let{
    runCatching{raw("POST","/api/auth/logout",buildJsonObject{put("refreshToken",tokens.refreshToken)},false)}
    error("That is another account. Sign in as ${state.value.me?.email?:"the account you used"} to send the changes saved on this phone.")
   }
   session=Session(serverUrl,me.id,tokens);persistSession()
   update{it.copy(me=me,workspaceId=state.value.workspaceId?.takeIf{id->me.workspaces.any{it.id==id}}?:me.defaultWorkspaceId?:me.workspaces.firstOrNull()?.id,sessionExpired=false)}
   initialized=true;refresh();if(foreground)startLiveSync()
  }catch(e:Exception){update{it.copy(error=e.message,loading=false)};throw e}
 }
 suspend fun serverInfo():ServerResponse=json.decodeFromJsonElement(raw("GET","/api/server",authenticated=false))
 suspend fun loadServerInfo(){val origin=serverUrl;try{val info=serverInfo();if(serverUrl==origin)update{it.copy(serverInfo=info)}}catch(e:Exception){if(e is CancellationException)throw e;update{it.copy(error=e.message)}}}
 suspend fun forgotPassword(email:String){raw("POST","/api/auth/forgot-password",buildJsonObject{put("email",email)},false)}
 suspend fun signOut(force:Boolean=false){
  operations.withLock{
   if(session!=null){
    val key=account();check(force||dao.pending(key).isEmpty()){ "There are unsent changes. Sync them or explicitly discard them before signing out." }
    runCatching{raw("POST","/api/auth/logout",buildJsonObject{put("refreshToken",session!!.tokens.refreshToken)},false)}
    dao.clearPending(key);dao.clearSnapshots(key)
   }
   syncJob?.cancel();hub?.stop()?.subscribe({},{});hub=null;vault.clear();session=null;initialized=false;baseEntries=emptyList();baseRunning=null;mutable.value=AppState()
  }
 }
 private fun persistSession(){session?.let{vault.write(json.encodeToString(Session.serializer(),it))}}
 private suspend fun token():String=authLock.withLock{
  // Another repository (a background worker) may have rotated the token.
  vault.read()?.let{runCatching{json.decodeFromString<Session>(it)}.getOrNull()}?.let{stored->if(stored.server==session?.server && stored.account==session?.account)session=stored}
  val s=session?:error("Sign in first.")
  if(Instant.parse(s.tokens.accessTokenExpiresAt).isAfter(Instant.now().plusSeconds(60)))return@withLock s.tokens.accessToken
  try{
   val result=raw("POST","/api/auth/refresh",buildJsonObject{put("refreshToken",s.tokens.refreshToken)},false)
   val tokens=json.decodeFromJsonElement<AuthTokensResponse>(result)
   session=s.copy(tokens=tokens);persistSession();tokens.accessToken
  }catch(e:ApiException){if(e.status==401){update{it.copy(sessionExpired=true,error="Your session expired. Sign in again to send your saved changes.")}};throw e}
 }
 /** [version], when given, is sent as If-Match: the server then refuses the write with 412 if the record has changed since. */
 suspend fun request(method:String,path:String,body:JsonObject?=null,version:String?=null):JsonElement{
  require(path.startsWith("/api/")){"Only API paths are supported."}
  val access=token()
  return try{raw(method,path,body,token=access,version=version)}catch(e:ApiException){
   if(e.status!=401)throw e
   invalidateAccess(access)
   raw(method,path,body,token=token(),version=version)
  }
 }
 private suspend fun invalidateAccess(rejected:String)=authLock.withLock{
  if(session?.tokens?.accessToken==rejected){session=session?.copy(tokens=session!!.tokens.copy(accessTokenExpiresAt=Instant.EPOCH.toString()));persistSession()}
 }
 private suspend fun raw(method:String,path:String,body:JsonObject?=null,authenticated:Boolean=true,token:String?=null,version:String?=null):JsonElement=withContext(Dispatchers.IO){
  val request=Request.Builder().url(serverUrl+path).apply{
   if(authenticated && token!=null)header("Authorization","Bearer $token")
   if(!version.isNullOrEmpty())header("If-Match","\"$version\"")
   header("Accept","application/json")
   method(method,if(method=="GET"||method=="HEAD")null else (body?.toString()?:"{}").toRequestBody("application/json".toMediaType()))
  }.build()
  http.newCall(request).execute().use{response->
   val text=response.body?.string().orEmpty()
   if(!response.isSuccessful)throw apiError(response.code,text)
   if(text.isBlank())JsonNull else json.parseToJsonElement(text)
  }
 }
 private fun apiError(code:Int,text:String):ApiException{
  if(code==402)return ApiException(code,ReadOnlyMessage)
  val obj=runCatching{json.parseToJsonElement(text).jsonObject}.getOrNull()
  val errors=obj?.get("errors")?.jsonObject?.values?.flatMap{runCatching{it.jsonArray.map{v->v.jsonPrimitive.content}}.getOrDefault(emptyList())}
  if(code==412 && obj?.get("type")?.jsonPrimitive?.contentOrNull=="urn:spindle:problem:entry-changed"){
   val current=obj["current"]?.let{runCatching{json.decodeFromJsonElement<TimeEntryResponse>(it)}.getOrNull()}
   if(current!=null)return ApiException(code,EntryChangedMessage,current)
  }
  return ApiException(code,errors?.takeIf{it.isNotEmpty()}?.joinToString("\n")?:obj?.get("detail")?.jsonPrimitive?.contentOrNull?:obj?.get("title")?.jsonPrimitive?.contentOrNull?:"Server returned $code.")
 }
 suspend fun download(method:String,path:String,body:JsonObject?=null):ExportFile=withContext(Dispatchers.IO){
  require(path.startsWith("/api/")){"Only API paths are supported."}
  val access=token()
  fun downloadRequest(accessToken:String)=Request.Builder().url(serverUrl+path).header("Authorization","Bearer $accessToken").method(method,if(method=="GET")null else (body?.toString()?:"{}").toRequestBody("application/json".toMediaType())).build()
  var response=http.newCall(downloadRequest(access)).execute()
  if(response.code==401){response.close();invalidateAccess(access);response=http.newCall(downloadRequest(token())).execute()}
  response.use{res->
   if(!res.isSuccessful)throw apiError(res.code,res.body?.string().orEmpty())
   val name=Regex("filename\\*?=(?:UTF-8''|\")?([^\";]+)").find(res.header("Content-Disposition").orEmpty())?.groupValues?.get(1)?.let{java.net.URLDecoder.decode(it,"UTF-8") }?:"spindle-export"
   val safe=File(name).name.replace(Regex("[^A-Za-z0-9._-]"),"_")
   val dir=File(context.cacheDir,"exports").apply{mkdirs()}
   val file=File(dir,"${UUID.randomUUID()}-$safe")
   res.body!!.byteStream().use{input->file.outputStream().use{input.copyTo(it)}}
   ExportFile(file,res.header("Content-Type")?:"application/octet-stream",safe)
  }
 }
 suspend fun refresh(){
  if(session==null)return
  operations.withLock{
   update{it.copy(loading=true,error=null)}
   try{
    flush()
    val me=json.decodeFromJsonElement<MeResponse>(request("GET","/api/me"))
    val selected=state.value.workspaceId?.takeIf{id->me.workspaces.any{it.id==id}}?:me.defaultWorkspaceId?:me.workspaces.firstOrNull()?.id
    update{it.copy(me=me,workspaceId=selected)}
    if(selected!=null){
     val projects=json.decodeFromJsonElement<List<ProjectResponse>>(request("GET",path()+"/projects"))
     val tags=json.decodeFromJsonElement<List<TagResponse>>(request("GET",path()+"/tags"))
     val clients=json.decodeFromJsonElement<List<ClientResponse>>(request("GET",path()+"/clients"))
     val settings=json.decodeFromJsonElement<WorkspaceSettingsResponse>(request("GET",path()+"/settings"))
     update{it.copy(projects=projects,tags=tags,clients=clients,settings=settings)}
     fetchEntries()
    }
    val current=request("GET","/api/me/time-entries/current")
    baseRunning=if(current is JsonNull)null else json.decodeFromJsonElement<TimeEntryResponse>(current)
    update{it.copy(offline=false,loading=false)};saveCache();overlay()
   }catch(e:CancellationException){throw e}catch(e:Exception){update{it.copy(loading=false,offline=e is IOException,error=e.message)};overlay()}
  }
 }
 suspend fun switchWorkspace(id:String){
  require(state.value.me?.workspaces?.any{it.id==id}==true){"Workspace not available."}
  operations.withLock{update{it.copy(workspaceId=id,projects=emptyList(),tags=emptyList(),clients=emptyList(),settings=null,entries=emptyList())};baseEntries=emptyList();restoreCache(preserveRunning=true);overlay()}
  hub?.let{runCatching{it.send("Watch",id)}}
  refresh()
 }
 suspend fun loadEntries(from:LocalDate,to:LocalDate){
  require(to>from && java.time.temporal.ChronoUnit.DAYS.between(from,to)<=365){"Choose a range of up to one year."}
  this.from=from;this.to=to
  operations.withLock{try{fetchEntries();saveCache();overlay();update{it.copy(offline=false)}}catch(e:Exception){if(e is CancellationException)throw e;update{it.copy(offline=e is IOException,error=e.message)}}}
 }
 private suspend fun fetchEntries(){
  val zone=ZoneId.of(state.value.me?.timeZone?:ZoneId.systemDefault().id)
  val url=(serverUrl+path()+"/time-entries").toHttpUrl().newBuilder().addQueryParameter("from",from.atStartOfDay(zone).toInstant().toString()).addQueryParameter("to",to.atStartOfDay(zone).toInstant().toString()).build()
  baseEntries=json.decodeFromJsonElement<List<TimeEntryResponse>>(request("GET",url.toString().removePrefix(serverUrl)))
 }
 suspend fun saveEntry(draft:EntryDraft,entry:TimeEntryResponse?=null){
  validateDraft(draft)
  val id=entry?.id?:UUID.randomUUID().toString();val ws=entry?.workspaceId?:workspace()
  val shares=shownShares(draft,entry)
  val optimistic=TimeEntryResponse(id=id,workspaceId=ws,description=draft.description,projectId=draft.projectId,taskId=draft.taskId,tagIds=draft.tagIds,isBillable=draft.isBillable,start=draft.start,stop=draft.stop,shares=shares,version=entry?.version.orEmpty())
  val body=json.encodeToJsonElement(draft).jsonObject.toMutableMap().apply{if(entry==null)put("id",JsonPrimitive(id));if(draft.describeParts==null)remove("describeParts")}
  queue(PendingChange(account=account(),workspaceId=ws,entryId=id,method=if(entry==null)"POST" else "PUT",path=path(ws)+"/time-entries"+(if(entry==null)"" else "/$id"),body=JsonObject(body).toString(),optimistic=json.encodeToString(TimeEntryResponse.serializer(),optimistic),version=entry?.version),repairEntry=entry?.id)
 }
 suspend fun startTimer(draft:EntryDraft){
  val now=Instant.now()
  saveEntry(draft.copy(start=now.toString(),stop=null))
 }
 suspend fun stopTimer(entry:TimeEntryResponse?=null,at:Instant=Instant.now()){
  val current=entry?:state.value.running?:return
  val stoppedAt=maxOf(at,Instant.parse(current.start))
  if(current.workspaceId==state.value.workspaceId)validateDraft(EntryDraft(description=current.description,projectId=current.projectId,taskId=current.taskId,tagIds=current.tagIds,isBillable=current.isBillable,shares=current.shares.map{TimeShareRequest(it.projectId,it.taskId,it.isBillable,it.percent)},start=current.start,stop=stoppedAt.toString()))
  queue(PendingChange(account=account(),workspaceId=current.workspaceId,entryId=current.id,method="POST",path=path(current.workspaceId)+"/time-entries/${current.id}/stop",body=buildJsonObject{put("stop",stoppedAt.toString())}.toString(),optimistic=json.encodeToString(TimeEntryResponse.serializer(),current.copy(stop=stoppedAt.toString())),version=current.version))
 }
 suspend fun deleteEntry(entry:TimeEntryResponse){queue(PendingChange(account=account(),workspaceId=entry.workspaceId,entryId=entry.id,method="DELETE",path=path(entry.workspaceId)+"/time-entries/${entry.id}",body=null,optimistic=null,version=entry.version))}
 private suspend fun queue(change:PendingChange,repairEntry:String?=null){
  operations.withLock{
   val id=db.withTransaction{
    var replacement=change
    if(repairEntry!=null){
     val queued=dao.pending(account()).filter{it.entryId==repairEntry}
     val rejected=queued.firstOrNull{it.error!=null}
     if(rejected!=null){
      if(rejected.method=="POST" && !rejected.path.endsWith("/stop")){
       val repairedBody=json.parseToJsonElement(change.body!!).jsonObject.toMutableMap().apply{put("id",JsonPrimitive(rejected.entryId))}
       replacement=change.copy(method="POST",path=rejected.path,body=JsonObject(repairedBody).toString())
      }
      for(previous in queued.filter{it.id>=rejected.id})dao.remove(previous.id)
     }
    }
    dao.add(replacement)
   }
   overlay();schedule()
   try{flush();saveCache();overlay()}catch(e:CancellationException){throw e}catch(e:Exception){update{it.copy(offline=e is IOException,error=e.message)};overlay();if(e is ApiException && e.status<500)throw e}
   dao.pending(account()).firstOrNull{it.id==id}?.error?.let{throw ApiException(400,it)}
  }
 }
 private suspend fun flush(){
  // Read again each time: a change sent moves the entry's later changes to the version it got.
  while(true){
   val change=dao.pending(account()).firstOrNull()?:break
   if(change.error!=null)break
   try{
    val result=request(change.method,change.path,change.body?.let{json.parseToJsonElement(it).jsonObject},change.version)
    val saved=if(change.method=="DELETE"||result==JsonNull)null else json.decodeFromJsonElement<TimeEntryResponse>(result)
    if(change.method=="DELETE")applyBase(change,null) else if(saved!=null)applyBase(change,saved)
    // Save the acknowledged server value before deleting its optimistic overlay.
    saveCache();dao.remove(change.id)
    if(saved!=null)for(later in dao.pending(account()).filter{it.entryId==change.entryId})dao.setVersion(later.id,saved.version)
   }catch(e:ApiException){
    if(e.status==404 && change.method=="DELETE"){applyBase(change,null);saveCache();dao.remove(change.id);continue}
    val current=e.current
    if(e.isEntryChanged && current!=null){
     // A timer renamed elsewhere can still be stopped: that overwrites nothing of theirs.
     if(change.path.endsWith("/stop") && current.stop==null && change.version!=current.version){dao.setVersion(change.id,current.version);continue}
     // Theirs is shown. Retry sends the change again on their version, keeping yours; Discard keeps theirs; Edit starts from theirs.
     applyBase(change,current);saveCache();dao.setVersion(change.id,current.version);dao.fail(change.id,e.message)
     update{it.copy(error=e.message)};break
    }
    if(e.status in listOf(400,402,403,404,409,422)){dao.fail(change.id,e.message);update{it.copy(error=if(e.isReadOnly)e.message else "A saved change was rejected: ${e.message}")};if(e.isReadOnly)learnReadOnly();break}
    throw e
   }
  }
 }
 /** A change refused as read-only means the organization became so since the person was loaded: load them again, so the app says so instead of failing change after change. */
 private suspend fun learnReadOnly(){
  val known=state.value.me?.let{me->val organization=me.workspaces.find{it.id==state.value.workspaceId}?.organizationId;me.organizations.any{it.id==organization && it.access=="readOnly"}}
  if(known==true)return
  try{val me=json.decodeFromJsonElement<MeResponse>(request("GET","/api/me"));update{it.copy(me=me)};saveCache()}catch(e:CancellationException){throw e}catch(_:Exception){}
 }
 private fun applyBase(change:PendingChange,entry:TimeEntryResponse?){
  if(change.workspaceId==state.value.workspaceId){baseEntries=baseEntries.filterNot{it.id==change.entryId}+listOfNotNull(entry)}
  if(baseRunning?.id==change.entryId)baseRunning=null
  if(entry?.stop==null && entry!=null){
   val previous=baseRunning
   if(previous!=null && previous.id!=entry.id)baseEntries=baseEntries.map{if(it.id==previous.id)it.copy(stop=entry.start) else it}
   baseRunning=entry
  }
 }
 private suspend fun overlay(){
  val changes=dao.pending(account());val entries=baseEntries.associateBy{it.id}.toMutableMap();var running=baseRunning
  // Rejected changes stay in the durable queue for user recovery, but are not presented as server truth.
  val blockedIds=changes.filter{it.error!=null}.map{it.entryId}.toSet()
  for(change in changes){
   if(change.entryId in blockedIds)continue
   val entry=change.optimistic?.let{json.decodeFromString<TimeEntryResponse>(it)}
   if(change.workspaceId==state.value.workspaceId){entries.remove(change.entryId);if(entry!=null)entries[entry.id]=entry}
   if(running?.id==change.entryId)running=null
   if(entry!=null && entry.stop==null){
    running?.takeIf{it.id!=entry.id}?.let{previous->entries[previous.id]?.let{entries[previous.id]=it.copy(stop=entry.start)}}
    running=entry
   }
  }
  val zone=ZoneId.of(state.value.me?.timeZone?:ZoneId.systemDefault().id)
  update{it.copy(entries=entries.values.filter{entry->val date=Instant.parse(entry.start).atZone(zone).toLocalDate();date>=from && date<to}.sortedByDescending{entry->entry.start},running=running,pendingCount=changes.size,failedChanges=changes.filter{c->c.error!=null})}
 }
 suspend fun discardFailedChange(id:Long){
  operations.withLock{
   db.withTransaction{
    val queued=dao.pending(account());val found=queued.firstOrNull{it.id==id};require(found?.error!=null){"Only rejected changes can be discarded."}
    for(change in queued.filter{it.entryId==found.entryId && it.id>=id})dao.remove(change.id)
   }
   overlay()
  }
  refresh()
 }
 suspend fun retryFailedChange(id:Long){operations.withLock{require(dao.pending(account()).any{it.id==id&&it.error!=null});dao.fail(id,null)};refresh()}
 fun clearError(){update{it.copy(error=null)}}
 private fun validateDraft(draft:EntryDraft){
  require(draft.description.length<=3000){"Description is too long."}
  val start=Instant.parse(draft.start);draft.stop?.let{require(Instant.parse(it)>start){"Stop must follow start."}}
  val rules=state.value.settings?:return
  val zone=ZoneId.of(state.value.me?.timeZone?:ZoneId.systemDefault().id)
  if(state.value.me?.workspaces?.firstOrNull{it.id==state.value.workspaceId}?.role != "admin")rules.lockedBefore?.let{require(start.atZone(zone).toLocalDate()>=LocalDate.parse(it)){"This day is locked."}}
  if(draft.stop!=null){require(!rules.requireDescription||(if(draft.describeParts==true&&(draft.shares?.size?:0)>1)draft.shares!!.all{!it.description.isNullOrBlank()} else draft.description.isNotBlank())){ "A description is required." };val parts=draft.shares?.takeIf{it.size>1}?:listOf(TimeShareRequest(projectId=draft.projectId,taskId=draft.taskId));for(part in parts){require(!rules.requireProject||part.projectId!=null){"A project is required."};val activeTasks=state.value.projects.firstOrNull{it.id==part.projectId}?.tasks?.any{!it.isDone}==true;require(!rules.requireTask||!activeTasks||part.taskId!=null){"A task is required on this project."}};require(!rules.requireTag||draft.tagIds.isNotEmpty()){ "A tag is required." }}
 }
 private suspend fun saveCache(){
  val me=state.value.me?:return;val ws=state.value.workspaceId
  val s=state.value
  val cached=CachedState(me,ws,s.projects,s.tags,s.clients,s.settings,baseEntries,baseRunning)
  db.withTransaction{
   dao.put(Snapshot(account(),"workspace:${ws.orEmpty()}",json.encodeToString(CachedState.serializer(),cached)))
   dao.put(Snapshot(account(),"last",ws.orEmpty()))
   dao.put(Snapshot(account(),"running",baseRunning?.let{json.encodeToString(TimeEntryResponse.serializer(),it)}?:"null"))
  }
 }
 private suspend fun restoreCache(preserveRunning:Boolean=false){
  val selected=state.value.workspaceId?:dao.get(account(),"last")?.value
  val cached=dao.get(account(),"workspace:${selected.orEmpty()}")?.let{json.decodeFromString<CachedState>(it.value)}?:return
  baseEntries=cached.entries
  if(!preserveRunning){val global=dao.get(account(),"running")?.value;baseRunning=if(global==null)cached.running else if(global=="null")null else json.decodeFromString<TimeEntryResponse>(global)}
  update{it.copy(me=cached.me,workspaceId=cached.workspaceId,projects=cached.projects,tags=cached.tags,clients=cached.clients,settings=cached.settings,offline=true)}
 }
 private fun schedule(){
  scheduleSync?.let{it();return}
  val constraints=Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()
  val request=OneTimeWorkRequestBuilder<SpindleSyncWorker>().setConstraints(constraints).setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,TimeUnit.SECONDS).build()
  WorkManager.getInstance(context).enqueueUniqueWork("spindle-outbox",ExistingWorkPolicy.KEEP,request)
 }
 fun onForeground(){
  foreground=true
  scope.launch{initialize();refresh();if(foreground)startLiveSync()}
 }
 fun onBackground(){
  val connection=synchronized(liveLock){
   foreground=false
   syncJob?.cancel();syncJob=null
   hub.also{hub=null}
  }
  connection?.stop()?.subscribe({},{})
 }
 private fun startLiveSync(){
  synchronized(liveLock){
   if(!foreground || session==null || syncJob?.isActive==true)return
   syncJob=scope.launch{
    var owned:HubConnection?=null
    try{
     while(isActive && foreground && session!=null){
      try{
       val current=owned?:HubConnectionBuilder.create(serverUrl+"/api/hubs/sync")
        .withAccessTokenProvider(Single.fromCallable{runBlocking{token()}}).build().also{candidate->
         owned=candidate
         candidate.on("Changed",{_:Any->
          if(foreground && hub===candidate)scope.launch{if(foreground && hub===candidate)refresh()}
         },Any::class.java)
         synchronized(liveLock){if(foreground && isActive)hub=candidate}
        }
       if(!isActive || !foreground || hub!==current)break
       if(current.connectionState.toString()=="DISCONNECTED"){
        current.start().blockingAwait()
        // Starting is blocking; an onStop may have detached this connection while
        // its handshake was still in flight. Close it before watching or refreshing.
        if(!isActive || !foreground || hub!==current){current.stop().subscribe({},{});break}
        state.value.workspaceId?.let{current.send("Watch",it)}
        if(isActive && foreground && hub===current)refresh()
       }
      }catch(e:CancellationException){throw e}catch(e:Exception){
       if(isActive && foreground)update{it.copy(offline=e is IOException)}
      }
      delay(15_000)
     }
    }finally{
     synchronized(liveLock){if(hub===owned)hub=null}
     owned?.stop()?.subscribe({},{})
    }
   }
  }
 }
 companion object {private val operations=Mutex();private val authLock=Mutex()}
}
class SpindleSyncWorker(context:Context,params:WorkerParameters):CoroutineWorker(context,params){
 override suspend fun doWork():Result{
  val repository=(applicationContext as com.spindletrack.android.SpindleApplication).repository
  return try{repository.initialize();repository.refresh();if(repository.state.value.offline||repository.state.value.pendingCount>0&&repository.state.value.failedChanges.isEmpty())Result.retry() else Result.success()}catch(e:Exception){if(e is CancellationException)throw e;Result.retry()}
 }
}
