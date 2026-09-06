#pragma once

#ifdef _WIN32
#define OPTIMUS_AEC_API __declspec(dllexport)
#else
#define OPTIMUS_AEC_API
#endif

#include <cstdint>

extern "C" {
OPTIMUS_AEC_API void* optimus_aec_create(int sample_rate);
OPTIMUS_AEC_API void optimus_aec_process_reverse(void* handle, const std::int16_t* pcm, int samples);
OPTIMUS_AEC_API void optimus_aec_process_capture(void* handle, const std::int16_t* input, std::int16_t* output, int samples);
OPTIMUS_AEC_API void optimus_aec_destroy(void* handle);
}
