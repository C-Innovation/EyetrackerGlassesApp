#include <MNN/Interpreter.hpp>
#include <MNN/Tensor.hpp>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _WIN32
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif

namespace {
constexpr int max_rank = 8;
struct Output {
    std::string name;
    std::unique_ptr<MNN::Tensor> host;
};
struct Context {
    std::unique_ptr<MNN::Interpreter> net;
    MNN::Session* session = nullptr;
    MNN::Tensor* input = nullptr;
    std::unique_ptr<MNN::Tensor> host_input;
    std::vector<Output> outputs;
    std::mutex gate;
    ~Context() {
        outputs.clear();
        host_input.reset();
        if (net && session) net->releaseSession(session);
    }
};
void set_error(char* destination, size_t capacity, const char* text) noexcept {
    if (destination && capacity) std::snprintf(destination, capacity, "%s", text ? text : "MNN error");
}
// Work with unpacked host tensors. elementSize() on NC4HW4 includes padding and
// is NOT necessarily the logical product of dimensions.
int count(const MNN::Tensor& tensor) {
    auto type = tensor.getType();
    if (type.code != halide_type_float || type.bits != 32 || type.lanes != 1)
        throw std::runtime_error("Only FP32 input/output tensors are supported");
    const auto shape = tensor.shape();
    if (shape.size() > max_rank) throw std::runtime_error("Tensor rank exceeds bridge limit");
    size_t result = 1;
    for (int dim : shape) {
        if (dim <= 0 || result > static_cast<size_t>(INT32_MAX / sizeof(float)) / static_cast<size_t>(dim))
            throw std::runtime_error("Dynamic, empty or oversized tensor is not supported");
        result *= static_cast<size_t>(dim);
    }
    if (tensor.usize() != result * sizeof(float))
        throw std::runtime_error("Expected contiguous unpacked FP32 host tensor");
    return static_cast<int>(result);
}
std::unique_ptr<MNN::Tensor> host_tensor(MNN::Tensor* tensor) {
    const auto type = tensor->getType();
    if (type.code != halide_type_float || type.bits != 32 || type.lanes != 1)
        throw std::runtime_error("Only FP32 input/output tensors are supported");
    // getDimensionType maps NC4HW4 to CAFFE: the host copy removes channel padding.
    auto host = std::make_unique<MNN::Tensor>(tensor, tensor->getDimensionType());
    count(*host);
    if (!host->host<float>()) throw std::runtime_error("Could not allocate host tensor");
    return host;
}
int info(const MNN::Tensor& tensor, int* dimensions, int capacity, int* elements, int* nhwc) {
    const auto shape = tensor.shape();
    if (!dimensions || capacity < static_cast<int>(shape.size()) || !elements || !nhwc) return -1;
    *elements = count(tensor);
    *nhwc = tensor.getDimensionType() == MNN::Tensor::TENSORFLOW ? 1 : 0;
    for (size_t i = 0; i < shape.size(); ++i) dimensions[i] = shape[i];
    return static_cast<int>(shape.size());
}
}

API int mnn_bridge_abi_version() noexcept { return 1; }

API void* mnn_create(const uint8_t* model, size_t model_size, int threads, int backend,
    char* error, size_t error_capacity) noexcept {
    set_error(error, error_capacity, "");
    try {
        if (!model || !model_size || model_size > static_cast<size_t>(INT32_MAX)
            || threads < 1 || threads > 64 || (backend != 0 && backend != 1))
            throw std::runtime_error("Invalid model buffer, thread count or backend");
        auto ctx = std::make_unique<Context>();
        // MNN copies the buffer. Loading bytes also handles Unicode paths on Windows.
        ctx->net.reset(MNN::Interpreter::createFromBuffer(model, model_size));
        if (!ctx->net) throw std::runtime_error("MNN could not load model data");
        MNN::BackendConfig cpu;
        cpu.precision = MNN::BackendConfig::Precision_High;
        MNN::ScheduleConfig config;
        config.numThread = threads;
        config.type = backend == 0 ? MNN_FORWARD_CPU : MNN_FORWARD_AUTO;
        config.backupType = MNN_FORWARD_CPU;
        config.backendConfig = &cpu;
        ctx->session = ctx->net->createSession(config);
        if (!ctx->session) throw std::runtime_error("MNN could not create session");
        const auto& inputs = ctx->net->getSessionInputAll(ctx->session);
        if (inputs.size() != 1) throw std::runtime_error("Expected exactly one model input");
        ctx->input = inputs.begin()->second;
        if (!ctx->input || ctx->input->dimensions() < 1)
            throw std::runtime_error("Model has no valid input tensor");
        ctx->host_input = host_tensor(ctx->input);
        return ctx.release();
    } catch (const std::exception& ex) { set_error(error, error_capacity, ex.what()); }
    catch (...) { set_error(error, error_capacity, "Native exception while creating MNN session"); }
    return nullptr;
}

API void mnn_destroy(void* handle) noexcept {
    // Managed SafeHandle prevents use-after-free; native callers must obey ownership.
    try { delete static_cast<Context*>(handle); } catch (...) { }
}
API int mnn_input_info(void* handle, int* dimensions, int capacity, int* elements, int* nhwc) noexcept {
    try {
        auto* ctx = static_cast<Context*>(handle);
        if (!ctx) return -1;
        std::lock_guard<std::mutex> lock(ctx->gate);
        return info(*ctx->host_input, dimensions, capacity, elements, nhwc);
    } catch (...) { return -1; }
}
API int mnn_run(void* handle, const float* values, int length, char* error, size_t error_capacity) noexcept {
    set_error(error, error_capacity, "");
    try {
        auto* ctx = static_cast<Context*>(handle);
        if (!ctx || !values) throw std::runtime_error("Invalid input pointer or session");
        std::lock_guard<std::mutex> lock(ctx->gate);
        ctx->outputs.clear(); // Failed runs never expose stale output.
        if (length != count(*ctx->host_input)) throw std::runtime_error("Input length differs from model shape");
        std::memcpy(ctx->host_input->host<float>(), values, static_cast<size_t>(length) * sizeof(float));
        if (!ctx->input->copyFromHostTensor(ctx->host_input.get())) throw std::runtime_error("Input copy failed");
        if (ctx->net->runSession(ctx->session) != MNN::NO_ERROR) throw std::runtime_error("MNN inference failed");
        std::vector<Output> outputs;
        for (const auto& item : ctx->net->getSessionOutputAll(ctx->session)) {
            if (!item.second) throw std::runtime_error("Null MNN output tensor");
            auto host = host_tensor(item.second);
            if (!item.second->copyToHostTensor(host.get())) throw std::runtime_error("Output copy failed");
            outputs.push_back({item.first, std::move(host)});
        }
        if (outputs.empty() || outputs.size() > 1024) throw std::runtime_error("Invalid number of MNN outputs");
        ctx->outputs = std::move(outputs);
        return 0;
    } catch (const std::exception& ex) { set_error(error, error_capacity, ex.what()); }
    catch (...) { set_error(error, error_capacity, "Native exception during MNN inference"); }
    return -1;
}
API int mnn_output_count(void* handle) noexcept {
    try {
        auto* ctx = static_cast<Context*>(handle);
        if (!ctx) return -1;
        std::lock_guard<std::mutex> lock(ctx->gate);
        return static_cast<int>(ctx->outputs.size());
    } catch (...) { return -1; }
}
API int mnn_output_info(void* handle, int index, int* dimensions, int capacity, int* elements,
    int* nhwc, char* name, size_t name_capacity) noexcept {
    try {
        auto* ctx = static_cast<Context*>(handle);
        if (!ctx) return -1;
        std::lock_guard<std::mutex> lock(ctx->gate);
        if (index < 0 || static_cast<size_t>(index) >= ctx->outputs.size()) return -1;
        const auto& output = ctx->outputs[index];
        set_error(name, name_capacity, output.name.c_str());
        return info(*output.host, dimensions, capacity, elements, nhwc);
    } catch (...) { return -1; }
}
API int mnn_output_copy(void* handle, int index, float* destination, int length) noexcept {
    try {
        auto* ctx = static_cast<Context*>(handle);
        if (!ctx || !destination) return -1;
        std::lock_guard<std::mutex> lock(ctx->gate);
        if (index < 0 || static_cast<size_t>(index) >= ctx->outputs.size()) return -1;
        const auto& host = *ctx->outputs[index].host;
        if (length != count(host)) return -1;
        std::memcpy(destination, host.host<float>(), static_cast<size_t>(length) * sizeof(float));
        return 0;
    } catch (...) { return -1; }
}
