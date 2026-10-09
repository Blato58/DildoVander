#include <cassert>
#include <functional>
#include <iostream>
#include <cstring>
#include "../src/features/voice_turn/EspIdfVoiceAudio.h"
#include "nvs.h"
namespace {
bool verified=false, profileB=false;
int mute=0, writes=0, gpioWrites=0;
std::function<void()> writing;
}
int gpio_set_direction(int pin,int mode) { assert(pin==16 && mode==GPIO_MODE_OUTPUT); return 0; }
int gpio_set_level(int pin,int level) { assert(pin==16); ++gpioWrites; mute=level; return 0; }
int nvs_open(const char* space,int,nvs_handle_t* handle) { assert(std::strcmp(space,"wand_audio")==0); *handle=1; return ESP_OK; }
int nvs_get_u8(nvs_handle_t,const char* key,uint8_t* value) { assert(std::strcmp(key,"profile_a_ok")==0); *value=verified; return ESP_OK; }
void nvs_close(nvs_handle_t) {}
int i2s_new_channel(const i2s_chan_config_t* config,i2s_chan_handle_t* tx,i2s_chan_handle_t* rx) {
 assert(config->dma_desc_num==8 && config->dma_frame_num==320 && config->auto_clear);
 *tx=reinterpret_cast<void*>(1); *rx=reinterpret_cast<void*>(2); return ESP_OK;
}
int i2s_channel_init_std_mode(i2s_chan_handle_t,const i2s_std_config_t* config) {
 assert(config->clk_cfg.rate==32000 && config->slot_cfg.bits==32 && config->slot_cfg.mode==2);
 if (profileB) assert(config->gpio_cfg.bclk==21 && config->gpio_cfg.ws==41 && config->gpio_cfg.din==42 && config->gpio_cfg.dout==47);
 else assert(config->gpio_cfg.bclk==4 && config->gpio_cfg.ws==5 && config->gpio_cfg.din==6 && config->gpio_cfg.dout==7);
 return ESP_OK;
}
int i2s_channel_enable(i2s_chan_handle_t) { return ESP_OK; }
int i2s_channel_read(i2s_chan_handle_t,void*,size_t,size_t*,unsigned) { return ESP_FAIL; }
int i2s_channel_write(i2s_chan_handle_t,const void*,size_t size,size_t* used,unsigned) {
 ++writes; *used=size; if(writing)writing(); return ESP_OK;
}
class SoftwareAudioBoard final : public AudioBoardPort {
 public:
  bool ready = false;
  bool AudioReady() const override { return ready; }
};
int main() {
 EspIdfVoiceAudio audio;
 assert(!audio.Initialize(false)); assert(!audio.Initialize(true));
 verified=true; assert(audio.Initialize(true)); assert(mute==0);
 const std::vector<uint8_t> pcm(2048,32);
 assert(!audio.Play(pcm));
 audio.Begin(); audio.Listen(false); assert(audio.Play(pcm)); assert(mute==1);
 assert(audio.Drain()); assert(mute==0);
 audio.Stop(); const int stoppedWrites=writes;
 audio.Listen(true); assert(!audio.Play(pcm)); assert(writes==stoppedWrites && mute==0);
 audio.Begin(); writing=[&]{audio.Stop();};
 assert(!audio.Play(pcm)); assert(mute==0); assert(!audio.Drain());
 writing={}; assert(!audio.Play(pcm)); // Late blocks never reopen this session.
 audio.Begin(); assert(audio.Play(pcm)); assert(audio.Drain()); assert(mute==0);
 const int legacyGpioWrites = gpioWrites;
 profileB=true;
 SoftwareAudioBoard expander;
 EspIdfVoiceAudio audioB;
 assert(!audioB.Initialize(true, &expander)); // Never falls back onto camera pins.
 expander.ready=true;
 assert(audioB.Initialize(true, &expander)); const int hardwareMute = mute;
 audioB.Begin(); assert(audioB.Play(pcm)); assert(mute==hardwareMute);
 audioB.Stop(); assert(mute==hardwareMute && !audioB.Play(pcm));
 audioB.Begin(); expander.ready=false; assert(!audioB.Play(pcm) && audioB.Failed());
 assert(gpioWrites == legacyGpioWrites); // Factory SD stays untouched throughout B.
 std::cout<<"ESP audio profile, sticky Stop, interrupted DMA and restart passed\n";
}
