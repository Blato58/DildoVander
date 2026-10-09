#include <cassert>
#include <cstdlib>
#include <cstring>
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
int64_t nowMs=0, stopAt=0;
void (*delayHook)()=nullptr;
bool proofAvailable=true;
std::string lastSsid;
struct Stop {};
}
const protocomm_security_t protocomm_security1{};
EspIdfWifiStation::~EspIdfWifiStation()=default;
bool EspIdfNetworkProfileStorage::Save(const NetworkProfileRecord&) { return true; }
StationNetworkStatus EspIdfStationNetwork::Status() {
  StationNetworkStatus status;
  status.phase="connecting"; status.result="connection_failed";
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
void protocomm_delete(protocomm_t* transport) { delete transport; }
esp_err_t protocomm_ble_start(protocomm_t*,const protocomm_ble_config_t* config) {
  ++opens; assert(config->service_uuid[12]==0x10 && config->service_uuid[13]==0x7f);
  assert(config->nu_lookup_count==3 && config->nu_lookup[2].uuid==0xff54);
  assert(std::strcmp(config->nu_lookup[2].name,"kufr-control")==0); return ESP_OK;
}
esp_err_t protocomm_ble_stop(protocomm_t*) { ++closes; return ESP_OK; }
esp_err_t protocomm_set_version(protocomm_t*,const char*,const char* version) {
  assert(std::strstr(version,"\"sec_ver\":1") && !std::strstr(version,"no_pop")); return ESP_OK;
}
esp_err_t protocomm_set_security(protocomm_t*,const char* name,const protocomm_security_t* security,const void* params) {
  assert(std::strcmp(name,"prov-session")==0 && security==&protocomm_security1);
  const auto* proof=static_cast<const protocomm_security1_params_t*>(params);
  assert(proof->len==32 && proof->data[0]=='a'); return ESP_OK;
}
esp_err_t protocomm_add_endpoint(protocomm_t*,const char*,protocomm_req_handler_t callback,void* context) {
  handler=callback; handlerContext=context; return ESP_OK;
}
void FillStatus(cJSON* response,void*) {
  cJSON_AddBoolToObject(response,"b",false);
  auto* ota=cJSON_AddObjectToObject(response,"o");
  cJSON_AddStringToObject(ota,"s","waiting-safe-idle");
  cJSON_AddStringToObject(ota,"a","1.2.3-build-with-long-version");
  cJSON_AddStringToObject(ota,"e","ota-trust-or-origin-not-provisioned");
  cJSON_AddBoolToObject(ota,"i",false);
}
std::string Request(const std::string& input,esp_err_t expected=ESP_OK) {
  uint8_t* out=nullptr; ssize_t length=0;
  const auto result=handler(1,reinterpret_cast<const uint8_t*>(input.data()),input.size(),&out,&length,handlerContext);
  assert(result==expected);
  if(result!=ESP_OK) { return {}; }
  assert(length<=512); std::string response(reinterpret_cast<char*>(out),length); cJSON_free(out); return response;
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
int main() {
  EspIdfStationNetwork station;
  EspIdfBleProvisioning absent(station);
  proofAvailable=false;
  assert(!absent.Begin(true,"unprovisioned","1.0.0",{}));
  assert(opens==0);
  proofAvailable=true;
  EspIdfBleProvisioning ble(station);
  std::string id(64,'i');
  assert(ble.Begin(true,id.c_str(),"123.123.123-build-version",{FillStatus,nullptr,nullptr}));
  assert(opens==0); // Begin and Tick do not block the control-owner task.
  ble.Tick(0,false);
  assert(opens==0);
  stopAt=100;
  try { lifecycle(taskContext); } catch(const Stop&) {}
  assert(opens==1 && ble.Active());
  auto status=Request("{\"v\":1,\"command\":\"status\"}");
  assert(status.find("\"n\":")!=std::string::npos && status.find("password")==std::string::npos);
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
}
