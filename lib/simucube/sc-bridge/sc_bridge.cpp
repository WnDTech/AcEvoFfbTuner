#include <cstdint>
#include <memory>
#include <string>
#include <chrono>

#pragma warning(push)
#pragma warning(disable : 4996)
#include <sc-api/api.h>
#include <sc-api/ffb.h>
#include <sc-api/led_control.h>
#include <sc-api/session.h>
#include <sc-api/device_info.h>
#include <sc-api/time.h>
#include <sc-api/core/device.h>
#pragma warning(pop)

extern "C" {

__declspec(dllexport) void* sc_create_api() {
    try {
        auto* api = new sc_api::Api();
        return api;
    } catch (...) {
        return nullptr;
    }
}

__declspec(dllexport) void sc_destroy_api(void* handle) {
    try {
        delete static_cast<sc_api::Api*>(handle);
    } catch (...) {}
}

__declspec(dllexport) void* sc_get_session(void* api_handle) {
    try {
        auto* api = static_cast<sc_api::Api*>(api_handle);
        auto session = api->getSession();
        if (!session) return nullptr;
        return new std::shared_ptr<sc_api::core::Session>(session);
    } catch (...) {
        return nullptr;
    }
}

__declspec(dllexport) void sc_release_session(void* session_ptr) {
    try {
        delete static_cast<std::shared_ptr<sc_api::core::Session>*>(session_ptr);
    } catch (...) {}
}

__declspec(dllexport) int sc_session_register_control(
    void* session_ptr,
    uint32_t control_flags,
    const char* id_name,
    const char* display_name,
    const char* author,
    const char* version) {
    try {
        auto& session = *static_cast<std::shared_ptr<sc_api::core::Session>*>(session_ptr);
        sc_api::core::ApiUserInformation info;
        info.display_name = display_name ? display_name : "";
        info.author = author ? author : "";
        info.version_string = version ? version : "";
        info.type = "sim_plugin";
        auto result = session->registerToControl(control_flags, id_name ? id_name : "AcEvoFfbTuner", info);
        return static_cast<int>(result);
    } catch (...) {
        return -1;
    }
}

__declspec(dllexport) int sc_session_get_state(void* session_ptr) {
    try {
        auto& session = *static_cast<std::shared_ptr<sc_api::core::Session>*>(session_ptr);
        return static_cast<int>(session->getState());
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) int sc_session_poll(void* session_ptr) {
    try {
        auto& session = *static_cast<std::shared_ptr<sc_api::core::Session>*>(session_ptr);
        return static_cast<int>(session->poll());
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) void* sc_create_ffb_pipeline(void* api_handle, uint16_t device_session_id) {
    try {
        auto* api = static_cast<sc_api::Api*>(api_handle);
        auto session = api->getSession();
        if (!session) return nullptr;
        sc_api::core::DeviceSessionId dev_id;
        dev_id.id = device_session_id;
        auto* pipeline = new sc_api::FfbPipeline(session, dev_id);
        return pipeline;
    } catch (...) {
        return nullptr;
    }
}

__declspec(dllexport) void sc_destroy_ffb_pipeline(void* pipeline_handle) {
    try {
        delete static_cast<sc_api::FfbPipeline*>(pipeline_handle);
    } catch (...) {}
}

__declspec(dllexport) int sc_ffb_configure_torque_nm(void* pipeline_handle, float gain) {
    try {
        auto* pipeline = static_cast<sc_api::FfbPipeline*>(pipeline_handle);
        sc_api::PipelineConfig config;
        config.offset_type = sc_api::OffsetType::torque_Nm;
        config.gain = gain;
        config.interpolation_type = sc_api::core::InterpolationType::linear;
        return pipeline->configure(config) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) int sc_ffb_generate_samples(
    void* pipeline_handle,
    int64_t start_timestamp_ns,
    int32_t sample_time_ns,
    const float* samples,
    uint32_t sample_count) {
    try {
        auto* pipeline = static_cast<sc_api::FfbPipeline*>(pipeline_handle);
        using Clock = sc_api::core::Clock;
        auto start = Clock::time_point(Clock::duration(start_timestamp_ns));
        auto duration = Clock::duration(sample_time_ns);
        return pipeline->generateEffect(start, duration, samples, sample_count) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) int sc_ffb_stop(void* pipeline_handle) {
    try {
        auto* pipeline = static_cast<sc_api::FfbPipeline*>(pipeline_handle);
        return pipeline->stop() ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) int sc_ffb_is_active(void* pipeline_handle) {
    try {
        auto* pipeline = static_cast<sc_api::FfbPipeline*>(pipeline_handle);
        return pipeline->isActive() ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) int sc_ffb_remove(void* pipeline_handle) {
    try {
        auto* pipeline = static_cast<sc_api::FfbPipeline*>(pipeline_handle);
        return pipeline->remove() ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) int64_t sc_get_timestamp_frequency_hz() {
    try {
        return sc_api::core::clock_source::getTimestampFrequencyHz();
    } catch (...) {
        return 10000000;
    }
}

__declspec(dllexport) int64_t sc_get_timestamp_now() {
    try {
        return sc_api::core::clock_source::getTimestamp();
    } catch (...) {
        return 0;
    }
}

}
