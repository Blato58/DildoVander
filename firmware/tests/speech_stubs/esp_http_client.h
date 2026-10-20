#pragma once
#include <cstdint>

using esp_err_t = int;
constexpr esp_err_t ESP_OK = 0;
constexpr esp_err_t ESP_FAIL = -1;
struct SpeechStubClient;
using esp_http_client_handle_t = SpeechStubClient*;
enum esp_http_client_event_id_t { HTTP_EVENT_ON_HEADER, HTTP_EVENT_ON_DATA };
enum esp_http_client_method_t { HTTP_METHOD_GET, HTTP_METHOD_POST, HTTP_METHOD_DELETE };
struct esp_http_client_event_t {
  esp_http_client_event_id_t event_id;
  esp_http_client_handle_t client;
  void* user_data;
  void* data = nullptr;
  int data_len = 0;
  char* header_key = nullptr;
  char* header_value = nullptr;
};
struct esp_http_client_config_t {
  const char* url = nullptr;
  esp_err_t (*crt_bundle_attach)(void*) = nullptr;
  bool disable_auto_redirect = false;
  bool skip_cert_common_name_check = true;
  int max_authorization_retries = 0;
  int timeout_ms = 0;
  esp_err_t (*event_handler)(esp_http_client_event_t*) = nullptr;
  void* user_data = nullptr;
};
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t*);
esp_err_t esp_http_client_set_header(esp_http_client_handle_t, const char*, const char*);
esp_err_t esp_http_client_set_method(esp_http_client_handle_t, esp_http_client_method_t);
esp_err_t esp_http_client_set_post_field(esp_http_client_handle_t, const char*, int);
esp_err_t esp_http_client_perform(esp_http_client_handle_t);
int esp_http_client_get_status_code(esp_http_client_handle_t);
int64_t esp_http_client_get_content_length(esp_http_client_handle_t);
esp_err_t esp_http_client_cleanup(esp_http_client_handle_t);
