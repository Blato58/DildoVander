#include <cassert>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <string>
#include "../src/features/network_provisioning/EspIdfBleProvisioning.h"
#include "protocomm_ble.h"
#include "protocomm_security1.h"
#include "nvs.h"

namespace {
TaskFunction_t lifecycle;
void* taskContext;
protocomm_req_handler_t handler;
void* handlerContext;
int opens=0, closes=0, configureCalls=0;
int stopFailures=0, endpointFailures=0, startFailures=0;
protocomm_t* pendingStop=nullptr;
int64_t nowMs=0, stopAt=0;
void (*delayHook)()=nullptr;
bool proofAvailable=true;
std::string lastSsid;
std::string currentIpv4;
const char* networkPhase = "connecting";
std::string availableVersion = "1.2.3-build-with-long-version";
struct Stop {};
}
const protocomm_security_t protocomm_security1{};
EspIdfWifiStation::~EspIdfWifiStation()=default;
bool EspIdfNetworkProfileStorage::Save(const NetworkProfileRecord&) { return true; }
std::string EspIdfStationNetwork::CurrentIpv4() const { return currentIpv4; }
StationNetworkStatus EspIdfStationNetwork::Status() {
  StationNetworkStatus status;
  status.phase=networkPhase; status.result="connection_failed";
  status.active=NetworkProfile::Fallback; status.lastSuccessful=NetworkProfile::Fallback;
  std::memset(status.hotspotSsid,'"',32); std::memset(status.fallbackSsid,'"',32);
  return status;
}
bool EspIdfStationNetwork::Configure(NetworkProfile,const char* ssid,const char* password) {
  ++configureCalls; lastSsid=ssid; assert(std::strcmp(password,"secret-password")==0); return true;
}
bool EspIdfStationNetwork::Delete(NetworkProfile) { return true; }
bool EspIdfStationNetwork::Test(NetworkProfile) { return true; }
BaseType_t xTaskCreate(TaskFunction_t entry,const char* name,uint32_t stack,void* context,unsigned,TaskHandle_t*) {
  assert(std::strcmp(name,"ble_provisioning")==0 && stack==6144); lifecycle=entry; taskContext=context; return pdPASS;
}
void vTaskDelete(TaskHandle_t) {}
void vTaskDelay(TickType_t ticks) {
  if(delayHook!=nullptr) { delayHook(); return; }
  nowMs+=ticks; if(nowMs>=stopAt) { throw Stop{}; }
}
int64_t esp_timer_get_time() { return nowMs*1000; }
esp_err_t esp_read_mac(uint8_t* mac,int) { std::memset(mac,1,6); return ESP_OK; }
esp_err_t esp_event_loop_create_default() { return ESP_ERR_INVALID_STATE; }
esp_err_t nvs_open(const char* name,int,nvs_handle_t* handle) { assert(std::strcmp(name,"wand_ble")==0); *handle=1; return proofAvailable ? ESP_OK : ESP_ERR_NVS_NOT_FOUND; }
esp_err_t nvs_get_str(nvs_handle_t,const char* name,char* data,size_t* length) {
  assert(std::strcmp(name,"pop")==0 && *length==33); std::memset(data,'a',32); data[32]=0; *length=33; return ESP_OK;
}
void nvs_close(nvs_handle_t) {}
protocomm_t* protocomm_new() { return new protocomm_t; }
void protocomm_delete(protocomm_t* transport) {
  assert(transport!=pendingStop); delete transport;
}
esp_err_t protocomm_ble_start(protocomm_t*,const protocomm_ble_config_t* config) {
  assert(pendingStop==nullptr); // A new radio must never overlap failed teardown.
  ++opens; assert(config->service_uuid[12]==0x10 && config->service_uuid[13]==0x7f);
  assert(config->nu_lookup_count==3 && config->nu_lookup[2].uuid==0xff54);
  assert(std::strcmp(config->nu_lookup[2].name,"kufr-control")==0);
  if(startFailures>0) { --startFailures; return ESP_ERR_INVALID_ARG; }
  return ESP_OK;
}
esp_err_t protocomm_ble_stop(protocomm_t* transport) {
  ++closes;
  if(stopFailures>0) { --stopFailures; pendingStop=transport; return ESP_ERR_INVALID_ARG; }
  if(pendingStop!=nullptr) { assert(pendingStop==transport); pendingStop=nullptr; }
  return ESP_OK;
}
esp_err_t protocomm_set_version(protocomm_t*,const char*,const char* version) {
  assert(std::strstr(version,"\"sec_ver\":1") && !std::strstr(version,"no_pop")); return ESP_OK;
}
esp_err_t protocomm_set_security(protocomm_t*,const char* name,const protocomm_security_t* security,const void* params) {
  assert(std::strcmp(name,"prov-session")==0 && security==&protocomm_security1);
  const auto* proof=static_cast<const protocomm_security1_params_t*>(params);
  assert(proof->len==32 && proof->data[0]=='a'); return ESP_OK;
}
esp_err_t protocomm_add_endpoint(protocomm_t*,const char*,protocomm_req_handler_t callback,void* context) {
  if(endpointFailures>0) { --endpointFailures; return ESP_ERR_INVALID_ARG; }
  handler=callback; handlerContext=context; return ESP_OK;
}
void FillStatus(cJSON* response,void*) {
  cJSON_AddBoolToObject(response,"b",false);
  auto* ota=cJSON_AddObjectToObject(response,"o");
  cJSON_AddStringToObject(ota,"s","waiting-safe-idle");
  cJSON_AddStringToObject(ota,"a",availableVersion.c_str());
  cJSON_AddStringToObject(ota,"e","ota-trust-or-origin-not-provisioned");
  cJSON_AddBoolToObject(ota,"i",false);
}
int pairCalls = 0;
int recoverCalls = 0;
const char* recoveryError = nullptr;
void FillPairingFields(cJSON* response) {
  cJSON_AddStringToObject(response, "id", std::string(64, 'i').c_str());
  cJSON_AddStringToObject(response, "host", "kufr-0123456789abcdef0123456789abcdef.local");
  cJSON_AddStringToObject(response, "spkiSha256", std::string(64, 'a').c_str());
  cJSON_AddStringToObject(response, "token", std::string(64, 'b').c_str());
}
const char* FillPair(cJSON* response, void*) {
  if (++pairCalls != 1) { return "already_paired"; }
  FillPairingFields(response);
  return nullptr;
}
const char* FillRecovery(cJSON* response, void*) {
  ++recoverCalls;
  FillPairingFields(response);
  return recoveryError;
}
std::string Request(const std::string& input,esp_err_t expected=ESP_OK) {
  uint8_t* out=nullptr; ssize_t length=0;
  const auto result=handler(1,reinterpret_cast<const uint8_t*>(input.data()),input.size(),&out,&length,handlerContext);
  assert(result==expected);
  if(result!=ESP_OK) { return {}; }
  assert(length<=512); std::string response(reinterpret_cast<char*>(out),length); cJSON_free(out); return response;
}
void AssertStatusIp(const std::string& response, const char* expected) {
  auto* root = cJSON_Parse(response.c_str());
  assert(root != nullptr);
  const auto* network = cJSON_GetObjectItemCaseSensitive(root, "n");
  const auto* ip = cJSON_GetObjectItemCaseSensitive(network, "ip");
  if (expected == nullptr) { assert(ip == nullptr); }
  else { assert(cJSON_IsString(ip) && std::strcmp(ip->valuestring, expected) == 0); }
  cJSON_Delete(root);
}
EspIdfBleProvisioning* windowOwner=nullptr;
unsigned windowStage=0;
void VerifyWindows() {
  switch(windowStage++) {
    case 0:
      assert(opens==2 && windowOwner->Active());
      windowOwner->Tick(nowMs,true);
      assert(closes==0); // Owner publication never stops transport synchronously.
      nowMs+=300001;
      return;
    case 1:
      assert(closes==1 && !windowOwner->Active());
      windowOwner->Tick(nowMs,false);
      ++nowMs;
      return;
    case 2:
      nowMs+=60001;
      return;
    case 3:
      assert(opens==3 && windowOwner->Active());
      nowMs+=300001;
      return;
    default:
      assert(opens==3 && closes==2 && !windowOwner->Active());
      throw Stop{};
  }
}
int baseOpens=0, baseCloses=0;
void VerifyStopRetry() {
  switch(windowStage++) {
    case 0:
      assert(windowOwner->Active() && opens==baseOpens+1);
      stopFailures=2;
      nowMs=300001;
      return;
    case 1:
      assert(!windowOwner->Active() && closes==baseCloses+1 && opens==baseOpens+1);
      Request("{\"v\":1,\"command\":\"status\"}",ESP_ERR_INVALID_ARG);
      nowMs+=999;
      return;
    case 2:
      assert(closes==baseCloses+1 && opens==baseOpens+1);
      ++nowMs;
      return;
    case 3:
      assert(closes==baseCloses+2 && !windowOwner->Active() && opens==baseOpens+1);
      nowMs+=1000;
      return;
    case 4:
      assert(closes==baseCloses+3 && windowOwner->Active() && opens==baseOpens+2);
      nowMs+=300001;
      return;
    case 5:
      assert(closes==baseCloses+4 && !windowOwner->Active());
      nowMs+=60001;
      return;
    default:
      assert(opens==baseOpens+2 && closes==baseCloses+4 && pendingStop==nullptr);
      throw Stop{};
  }
}
void VerifyInitialCleanup() {
  switch(windowStage++) {
    case 0:
      assert(opens==baseOpens+1 && closes==baseCloses+1 && !windowOwner->Active());
      nowMs=999;
      return;
    case 1:
      assert(closes==baseCloses+1 && opens==baseOpens+1);
      nowMs=1000;
      return;
    case 2:
      assert(closes==baseCloses+2 && pendingStop!=nullptr);
      nowMs=2000;
      return;
    case 3:
      assert(closes==baseCloses+3 && pendingStop==nullptr && opens==baseOpens+2);
      assert(windowOwner->Active());
      nowMs=299999;
      return;
    case 4:
      assert(windowOwner->Active() && opens==baseOpens+2);
      nowMs=300000; // Startup delay must not extend the original boot deadline.
      return;
    case 5:
      assert(!windowOwner->Active() && closes==baseCloses+4);
      windowOwner->Tick(nowMs,false);
      ++nowMs;
      return;
    case 6:
      nowMs+=60000;
      return;
    case 7:
      assert(windowOwner->Active() && opens==baseOpens+3);
      nowMs+=300000;
      return;
    default:
      assert(!windowOwner->Active() && opens==baseOpens+3 && closes==baseCloses+5);
      throw Stop{};
  }
}
void VerifyRecoveryOpenFailure() {
  switch(windowStage++) {
    case 0:
      assert(windowOwner->Active());
      endpointFailures=1;
      nowMs=300001;
      return;
    case 1:
      assert(!windowOwner->Active() && opens==baseOpens+2 && closes==baseCloses+2);
      nowMs+=999;
      return;
    case 2:
      assert(opens==baseOpens+2);
      ++nowMs;
      return;
    case 3:
      assert(windowOwner->Active() && opens==baseOpens+3);
      nowMs+=300001;
      return;
    default:
      assert(!windowOwner->Active() && opens==baseOpens+3 && closes==baseCloses+3);
      throw Stop{};
  }
}
void VerifyInitialStartRetry() {
  switch(windowStage++) {
    case 0:
      assert(opens==baseOpens+1 && !windowOwner->Active());
      nowMs=999;
      return;
    case 1:
      assert(opens==baseOpens+1 && closes==baseCloses);
      nowMs=1000;
      return;
    case 2:
      assert(opens==baseOpens+2 && windowOwner->Active());
      nowMs=299999;
      return;
    case 3:
      assert(windowOwner->Active());
      Request("{\"v\":1,\"command\":\"status\"}");
      assert(Request("{\"v\":1,\"command\":\"recover-local\",\"confirm\":true}") ==
             "{\"v\":1,\"ok\":false,\"error\":\"pairing_unavailable\"}");
      nowMs=300000;
      return;
    case 4:
      assert(!windowOwner->Active() && closes==baseCloses+1);
      nowMs=600000;
      return;
    default:
      assert(opens==baseOpens+2 && closes==baseCloses+1 && !windowOwner->Active());
      throw Stop{};
  }
}
void VerifyInitialRetryDeadline() {
  switch(windowStage++) {
    case 0:
      assert(opens==baseOpens+1 && !windowOwner->Active());
      nowMs=299000;
      return;
    case 1:
      assert(opens==baseOpens+2 && !windowOwner->Active());
      nowMs=299999;
      return;
    case 2:
      assert(opens==baseOpens+2);
      nowMs=300000;
      return;
    case 3:
      assert(opens==baseOpens+2 && !windowOwner->Active());
      nowMs=600000;
      return;
    case 4:
      assert(opens==baseOpens+2); // No unbounded initial retries after expiry.
      startFailures=0;
      windowOwner->Tick(nowMs,false);
      ++nowMs;
      return;
    case 5:
      nowMs+=60000;
      return;
    case 6:
      assert(opens==baseOpens+3 && windowOwner->Active());
      nowMs+=300000;
      return;
    default:
      assert(opens==baseOpens+3 && closes==baseCloses+1 && !windowOwner->Active());
      throw Stop{};
  }
}
void LifecycleRegression(EspIdfStationNetwork& station,void (*hook)(),bool failInitial,
                         bool networkReady=false,int failedStarts=0) {
  EspIdfBleProvisioning owner(station);
  nowMs=0; windowStage=0; baseOpens=opens; baseCloses=closes;
  endpointFailures=failInitial ? 1 : 0;
  stopFailures=failInitial ? 2 : 0;
  startFailures=failedStarts;
  assert(owner.Begin(true,"id","1.0.0",{}));
  windowOwner=&owner; delayHook=hook;
  owner.Tick(nowMs,networkReady);
  try { lifecycle(taskContext); assert(false); } catch(const Stop&) {}
  assert(pendingStop==nullptr);
}
int main() {
  EspIdfStationNetwork station;
  EspIdfBleProvisioning absent(station);
  proofAvailable=false;
  assert(!absent.Begin(true,"unprovisioned","1.0.0",{}));
  assert(opens==0);
  proofAvailable=true;
  EspIdfBleProvisioning ble(station);
  std::string id(64,'i');
  assert(ble.Begin(true,id.c_str(),"123.123.123-build-version",{FillStatus,nullptr,nullptr,FillPair,FillRecovery}));
  assert(opens==0); // Begin and Tick do not block the control-owner task.
  ble.Tick(0,false);
  assert(opens==0);
  stopAt=100;
  try { lifecycle(taskContext); } catch(const Stop&) {}
  assert(opens==1 && ble.Active());
  auto status=Request("{\"v\":1,\"command\":\"status\"}");
  assert(status.find("\"n\":")!=std::string::npos && status.find("password")==std::string::npos);
  assert(status.find("token") == std::string::npos && status.find("spkiSha256") == std::string::npos);
  AssertStatusIp(status, nullptr);
  networkPhase = "connected";
  currentIpv4 = "192.168.255.254";
  status = Request("{\"v\":1,\"command\":\"status\"}");
  AssertStatusIp(status, currentIpv4.c_str());
  const std::string originalAvailableVersion = availableVersion;
  availableVersion = "1.2.3-" + std::string(58, 'a');
  status = Request("{\"v\":1,\"command\":\"status\"}");
  AssertStatusIp(status, nullptr); // Omit only the optional hint to retain the 512-byte status.
  assert(status.find(availableVersion) != std::string::npos);
  auto* boundedStatus = cJSON_Parse(status.c_str());
  const auto* network = cJSON_GetObjectItemCaseSensitive(boundedStatus, "n");
  assert(std::strlen(cJSON_GetObjectItemCaseSensitive(network, "h")->valuestring) == 32);
  assert(std::strlen(cJSON_GetObjectItemCaseSensitive(network, "f")->valuestring) == 32);
  cJSON_Delete(boundedStatus);
  availableVersion = originalAvailableVersion;
  for (const char* phase : {"offline", "connecting"}) {
    networkPhase = phase;
    status = Request("{\"v\":1,\"command\":\"status\"}");
    AssertStatusIp(status, nullptr);
  }
  networkPhase = "connected";
  currentIpv4.clear();
  status = Request("{\"v\":1,\"command\":\"status\"}");
  AssertStatusIp(status, nullptr);
  networkPhase = "connecting";
  auto paired = Request("{\"v\":2,\"command\":\"pair-local\"}");
  assert(paired == "{\"v\":1,\"ok\":false,\"error\":\"invalid_request\"}" && pairCalls == 0);
  paired = Request("{\"v\":1,\"command\":\"pair-local\",\"confirm\":true}");
  assert(paired.find("invalid_request") != std::string::npos && pairCalls == 0);
  paired = Request("{\"v\":1,\"command\":\"pair-local\"}");
  assert(paired.find("\"ok\":true") != std::string::npos && paired.find("\"token\":") != std::string::npos);
  assert(paired.find("\"host\":") != std::string::npos && paired.find("\"spkiSha256\":") != std::string::npos);
  assert(paired.find("\"n\":") == std::string::npos && paired.find("\"o\":") == std::string::npos);
  paired = Request("{\"v\":1,\"command\":\"pair-local\"}");
  assert(paired == "{\"v\":1,\"ok\":false,\"error\":\"already_paired\"}" && pairCalls == 2);
  for (const char* invalid : {
      "{\"v\":1,\"command\":\"recover-local\"}",
      "{\"v\":1,\"command\":\"recover-local\",\"confirm\":false}",
      "{\"v\":1,\"command\":\"recover-local\",\"confirm\":1}",
      "{\"v\":1,\"command\":\"recover-local\",\"confirm\":\"true\"}",
      "{\"v\":1,\"command\":\"recover-local\",\"confirm\":true,\"extra\":true}",
      "{\"v\":1,\"command\":\"recover-local\",\"confirm\":true,\"confirm\":true}",
      "{\"v\":2,\"command\":\"recover-local\",\"confirm\":true}"}) {
    assert(Request(invalid) == "{\"v\":1,\"ok\":false,\"error\":\"invalid_request\"}");
    assert(recoverCalls == 0 && pairCalls == 2);
  }
  auto recovered = Request("{\"v\":1,\"command\":\"recover-local\",\"confirm\":true}");
  assert(recoverCalls == 1 && pairCalls == 2);
  assert(recovered.find("\"ok\":true") != std::string::npos);
  assert(recovered.find("\"id\":") != std::string::npos && recovered.find("\"host\":") != std::string::npos);
  assert(recovered.find("\"spkiSha256\":") != std::string::npos && recovered.find("\"token\":") != std::string::npos);
  assert(recovered.find("\"n\":") == std::string::npos && recovered.find("\"o\":") == std::string::npos);
  recoveryError = "busy";
  assert(Request("{\"v\":1,\"command\":\"recover-local\",\"confirm\":true}") ==
         "{\"v\":1,\"ok\":false,\"error\":\"busy\"}");
  assert(recoverCalls == 2 && pairCalls == 2);
  status = Request("{\"v\":1,\"command\":\"status\"}");
  assert(status.find("token") == std::string::npos && status.find("spkiSha256") == std::string::npos);
  auto result=Request("{\"v\":1,\"command\":\"configure\",\"profile\":\"hotspot\",\"ssid\":\"Owner iPhone\",\"password\":\"secret-password\"}");
  assert(configureCalls==1 && lastSsid=="Owner iPhone" && result.find("secret-password")==std::string::npos);
  Request(std::string(513,'x'),ESP_ERR_INVALID_ARG);
  Request("{\"v\":1,\"command\":\"status\",\"nested\":[[[[]]]]}",ESP_ERR_INVALID_ARG);
  Request("{\"v\":1,\"command\":\"configure\",\"password\":\"foo\\u0000bar\"}",ESP_ERR_INVALID_ARG);
  result=Request("{\"v\":1,\"v\":1,\"command\":\"status\"}");
  assert(result.find("invalid_request")!=std::string::npos);
  nowMs=300001;
  Request("{\"v\":1,\"command\":\"status\"}",ESP_ERR_INVALID_ARG);
  EspIdfBleProvisioning bounded(station);
  assert(bounded.Begin(true,"id","1.0.0",{}));
  windowOwner=&bounded;
  delayHook=VerifyWindows;
  try { lifecycle(taskContext); assert(false); } catch(const Stop&) {}
  assert(windowStage==5);
  LifecycleRegression(station,VerifyStopRetry,false);
  LifecycleRegression(station,VerifyInitialCleanup,true,true);
  LifecycleRegression(station,VerifyRecoveryOpenFailure,false);
  LifecycleRegression(station,VerifyInitialStartRetry,false,true,1);
  LifecycleRegression(station,VerifyInitialRetryDeadline,false,true,100000);
}
