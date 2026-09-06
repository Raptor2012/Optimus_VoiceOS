cmake_minimum_required(VERSION 3.20)
project(optimus_webrtc_aec LANGUAGES CXX)

option(OPTIMUS_WEBRTC_AVAILABLE "Build against a checked-out WebRTC tree" OFF)
add_library(optimus_webrtc_aec SHARED webrtc_aec_bridge.cpp)
target_compile_features(optimus_webrtc_aec PRIVATE cxx_std_17)
target_include_directories(optimus_webrtc_aec PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

if (OPTIMUS_WEBRTC_AVAILABLE)
    if (NOT WEBRTC_ROOT)
        message(FATAL_ERROR "Set WEBRTC_ROOT to the WebRTC checkout when enabling APM")
    endif()
    target_compile_definitions(optimus_webrtc_aec PRIVATE OPTIMUS_WEBRTC_AVAILABLE=1)
    target_include_directories(optimus_webrtc_aec PRIVATE "${WEBRTC_ROOT}")
    # The WebRTC build exports the audio_processing target from its GN-generated project.
    target_link_libraries(optimus_webrtc_aec PRIVATE audio_processing)
endif()
