#include <cassert>
#include <cstring>
#include <string>
#include "../src/features/network_provisioning/EspIdfNetworkProfileStorage.h"
#include "nvs.h"

namespace {
esp_err_t openResult=ESP_OK, setResult=ESP_OK;
esp_err_t ssidResult=ESP_OK, passwordResult=ESP_OK;
unsigned failingCommit=0, commits=0, closes=0;
bool haveBlob=false, legacySsid=true, legacyPassword=true;
NetworkProfileRecord durable, pending;
std::string calls;
void Reset() {
  openResult=setResult=ssidResult=passwordResult=ESP_OK;
  failingCommit=commits=closes=0;
  haveBlob=false; legacySsid=legacyPassword=true;
  durable=NetworkProfileRecord{}; pending=NetworkProfileRecord{}; calls.clear();
}
}
esp_err_t nvs_open(const char* name,int mode,nvs_handle_t* handle) {
  assert(std::strcmp(name,"wand_network")==0);
  assert(mode==NVS_READONLY || mode==NVS_READWRITE);
  calls+="open,"; *handle=1; return openResult;
}
esp_err_t nvs_get_blob(nvs_handle_t,const char* key,void* data,size_t* length) {
  assert(std::strcmp(key,"profiles_v1")==0 && *length==sizeof(durable));
  if(!haveBlob) { return ESP_ERR_NVS_NOT_FOUND; }
  std::memcpy(data,&durable,sizeof(durable)); return ESP_OK;
}
esp_err_t nvs_set_blob(nvs_handle_t,const char* key,const void* data,size_t length) {
  assert(std::strcmp(key,"profiles_v1")==0 && length==sizeof(pending));
  calls+="set,";
  if(setResult==ESP_OK) { std::memcpy(&pending,data,length); }
  return setResult;
}
esp_err_t nvs_commit(nvs_handle_t) {
  calls+="commit,"; ++commits;
  if(commits==failingCommit) { return ESP_ERR_INVALID_ARG; }
  durable=pending; haveBlob=true; return ESP_OK;
}
esp_err_t nvs_erase_key(nvs_handle_t,const char* key) {
  // Targeted cleanup must never touch backend_host or any other namespace/key.
  const bool ssid=std::strcmp(key,"ssid")==0;
  assert(ssid || std::strcmp(key,"password")==0);
  calls+=ssid ? "ssid," : "password,";
  const auto result=ssid ? ssidResult : passwordResult;
  if(result==ESP_OK || result==ESP_ERR_NVS_NOT_FOUND) {
    (ssid ? legacySsid : legacyPassword)=false;
  }
  return result;
}
void nvs_close(nvs_handle_t) { calls+="close,"; ++closes; }

int main() {
  EspIdfNetworkProfileStorage storage;
  NetworkProfileRecord record;
  assert(NetworkProfiles::MakeCandidate("New phone","new-password",record.hotspot));
  Reset();
  assert(storage.Save(record));
  assert(calls=="open,set,commit,ssid,password,commit,close,");
  assert(haveBlob && !legacySsid && !legacyPassword && closes==1);

  Reset(); openResult=ESP_ERR_INVALID_ARG;
  assert(!storage.Save(record)); assert(calls=="open," && closes==0);
  Reset(); setResult=ESP_ERR_INVALID_ARG;
  assert(!storage.Save(record)); assert(calls=="open,set,close," && commits==0);
  assert(legacySsid && legacyPassword && !haveBlob);
  Reset(); failingCommit=1;
  assert(!storage.Save(record)); assert(calls=="open,set,commit,close,");
  assert(legacySsid && legacyPassword && !haveBlob);

  for(unsigned failure=0; failure<3; ++failure) {
    Reset();
    if(failure==0) { ssidResult=ESP_ERR_INVALID_ARG; }
    else if(failure==1) { passwordResult=ESP_ERR_INVALID_ARG; }
    else { failingCommit=2; }
    assert(!storage.Save(record));
    assert(calls=="open,set,commit,ssid,password,commit,close," && closes==1);
    // The new blob is durable before cleanup, even when cleanup reports failure.
    assert(haveBlob && std::strcmp(durable.hotspot.ssid,"New phone")==0);
  }
  for(unsigned missing=1; missing<=3; ++missing) {
    Reset();
    if((missing&1)!=0) { ssidResult=ESP_ERR_NVS_NOT_FOUND; }
    if((missing&2)!=0) { passwordResult=ESP_ERR_NVS_NOT_FOUND; }
    assert(storage.Save(record)); assert(commits==2 && closes==1);
  }

  // The pure profile owner restores its prior blob after cleanup failure, even
  // when cleanup fails again after the rollback blob has already committed.
  Reset();
  NetworkProfileRecord old;
  assert(NetworkProfiles::MakeCandidate("Old phone","old-password",old.hotspot));
  assert(storage.Save(old));
  NetworkProfiles profiles(storage);
  assert(profiles.Load(old));
  passwordResult=ESP_ERR_INVALID_ARG;
  assert(!profiles.CommitReplacement(NetworkProfile::Hotspot,record.hotspot));
  assert(std::strcmp(profiles.Get(NetworkProfile::Hotspot)->ssid,"Old phone")==0);
  assert(std::strcmp(durable.hotspot.ssid,"Old phone")==0);
  NetworkProfileRecord loaded; bool missing=true;
  assert(storage.Load(loaded,missing) && !missing);
  assert(std::strcmp(loaded.hotspot.ssid,"Old phone")==0);

  Reset(); record.version=2;
  assert(!storage.Save(record)); assert(calls.empty());
}
