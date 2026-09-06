#include "webrtc_aec_bridge.h"

#include <algorithm>
#include <memory>

// The WebRTC tree is intentionally supplied by the Windows build environment rather than
// checked into this personal-use repository.  Define OPTIMUS_WEBRTC_AVAILABLE and add its
// include/library paths in CMake to enable the real APM implementation.
#if defined(OPTIMUS_WEBRTC_AVAILABLE)
#include "api/audio/audio_processing.h"
#include "modules/audio_processing/include/audio_processing.h"

struct OptimusAec {
    std::unique_ptr<webrtc::AudioProcessing> apm;
    webrtc::StreamConfig config;

    explicit OptimusAec(int sample_rate)
        : apm(webrtc::AudioProcessingBuilder().Create()), config(sample_rate, 1, false) {
        webrtc::AudioProcessing::Config options;
        options.echo_canceller.enabled = true;
        options.noise_suppression.enabled = true;
        apm->ApplyConfig(options);
    }
};
#else
struct OptimusAec {};
#endif

extern "C" void* optimus_aec_create(int sample_rate) {
    if (sample_rate != 16000) return nullptr;
    try {
        return new OptimusAec(sample_rate);
    } catch (...) {
        return nullptr;
    }
}

extern "C" void optimus_aec_process_reverse(void* handle, const std::int16_t* pcm, int samples) {
    if (!handle || !pcm || samples <= 0) return;
#if defined(OPTIMUS_WEBRTC_AVAILABLE)
    auto* state = static_cast<OptimusAec*>(handle);
    state->apm->ProcessReverseStream(pcm, state->config, state->config,
                                      const_cast<std::int16_t*>(pcm));
#else
    (void)samples;
#endif
}

extern "C" void optimus_aec_process_capture(void* handle, const std::int16_t* input,
                                               std::int16_t* output, int samples) {
    if (!input || !output || samples <= 0) return;
#if defined(OPTIMUS_WEBRTC_AVAILABLE)
    auto* state = static_cast<OptimusAec*>(handle);
    state->apm->ProcessStream(input, state->config, state->config, output);
#else
    std::copy(input, input + samples, output);
    (void)handle;
#endif
}

extern "C" void optimus_aec_destroy(void* handle) {
    delete static_cast<OptimusAec*>(handle);
}
