#pragma once

#include <stddef.h>
#include <stdint.h>

using esp_err_t = int;
constexpr esp_err_t ESP_OK = 0;
constexpr esp_err_t ESP_FAIL = -1;
using httpd_handle_t = void*;
using httpd_free_ctx_fn_t = void (*)(void*);
using httpd_work_fn_t = void (*)(void*);
constexpr int HTTP_GET = 0;
constexpr int HTTP_POST = 1;

struct httpd_req_t {
  httpd_handle_t handle = nullptr;
  const char* uri = "/v1/control";
  void* user_ctx = nullptr;
  void* sess_ctx = nullptr;
  httpd_free_ctx_fn_t free_ctx = nullptr;
  size_t content_len = 0;
};

struct httpd_uri_t {
  const char* uri;
  int method;
  esp_err_t (*handler)(httpd_req_t*);
  void* user_ctx;
  bool is_websocket;
  bool handle_ws_control_frames;
  esp_err_t (*ws_pre_handshake_cb)(httpd_req_t*);
  esp_err_t (*ws_post_handshake_cb)(httpd_req_t*);
};

enum httpd_ws_type_t {
  HTTPD_WS_TYPE_CONTINUE = 0,
  HTTPD_WS_TYPE_TEXT = 1,
  HTTPD_WS_TYPE_BINARY = 2,
  HTTPD_WS_TYPE_CLOSE = 8,
  HTTPD_WS_TYPE_PING = 9,
  HTTPD_WS_TYPE_PONG = 10,
};

struct httpd_ws_frame_t {
  bool final;
  bool fragmented;
  httpd_ws_type_t type;
  uint8_t* payload;
  size_t len;
};

struct httpd_config_t {
  size_t max_open_sockets;
  size_t max_uri_handlers;
  bool lru_purge_enable;
  int recv_wait_timeout;
  int send_wait_timeout;
  size_t max_req_hdr_len;
  size_t max_uri_len;
};
constexpr int HTTPD_SSL_TRANSPORT_SECURE = 0;
struct httpd_ssl_config_t {
  httpd_config_t httpd;
  int transport_mode;
  uint16_t port_secure;
  const uint8_t* servercert;
  size_t servercert_len;
  const uint8_t* prvtkey_pem;
  size_t prvtkey_len;
  uint32_t tls_handshake_timeout_ms;
};
#define HTTPD_SSL_CONFIG_DEFAULT() {}

esp_err_t httpd_ssl_start(httpd_handle_t*, const httpd_ssl_config_t*);
esp_err_t httpd_ssl_stop(httpd_handle_t);
esp_err_t httpd_register_uri_handler(httpd_handle_t, const httpd_uri_t*);
size_t httpd_req_get_hdr_value_len(httpd_req_t*, const char*);
esp_err_t httpd_req_get_hdr_value_str(httpd_req_t*, const char*, char*, size_t);
int httpd_req_to_sockfd(httpd_req_t*);
esp_err_t httpd_resp_set_status(httpd_req_t*, const char*);
esp_err_t httpd_resp_set_hdr(httpd_req_t*, const char*, const char*);
esp_err_t httpd_resp_set_type(httpd_req_t*, const char*);
int httpd_req_recv(httpd_req_t*, char*, size_t);
esp_err_t httpd_resp_send(httpd_req_t*, const char*, int);
esp_err_t httpd_ws_recv_frame(httpd_req_t*, httpd_ws_frame_t*, size_t);
esp_err_t httpd_ws_send_frame(httpd_req_t*, httpd_ws_frame_t*);
esp_err_t httpd_ws_send_frame_async(httpd_handle_t, int, httpd_ws_frame_t*);
esp_err_t httpd_queue_work(httpd_handle_t, httpd_work_fn_t, void*);
void* httpd_sess_get_ctx(httpd_handle_t, int);
