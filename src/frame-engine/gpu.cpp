#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstring>
#include <memory>
#include <algorithm>
#include "Compose.h"
#include "Estimate.h"

using Microsoft::WRL::ComPtr;

namespace {
struct Parameters {
    int width, height, outputWidth, outputHeight, radius, direction;
    float phase, sharpen;
};

struct Texture {
    ComPtr<ID3D11Texture2D> data;
    ComPtr<ID3D11ShaderResourceView> read;
    ComPtr<ID3D11UnorderedAccessView> write;
};

struct Engine {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> estimate, compose;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11SamplerState> sampler;
    Texture previous, next, forward, backward, output, staging;
    Parameters size{};

    bool initialize() {
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, nullptr, &context))) return false;
        if (FAILED(device->CreateComputeShader(shaderEstimate, sizeof(shaderEstimate), nullptr, &estimate)) ||
            FAILED(device->CreateComputeShader(shaderCompose, sizeof(shaderCompose), nullptr, &compose))) return false;
        const D3D11_BUFFER_DESC desc{sizeof(Parameters), D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0};
        if (FAILED(device->CreateBuffer(&desc, nullptr, &constants))) return false;
        D3D11_SAMPLER_DESC sampling{};
        sampling.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sampling.AddressU = sampling.AddressV = sampling.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampling.ComparisonFunc = D3D11_COMPARISON_NEVER;
        sampling.MaxLOD = D3D11_FLOAT32_MAX;
        return SUCCEEDED(device->CreateSamplerState(&sampling, &sampler));
    }

    bool create(Texture& texture, int w, int h, DXGI_FORMAT format, UINT bindings, bool cpu = false) {
        texture = {};
        const D3D11_TEXTURE2D_DESC desc{static_cast<UINT>(w), static_cast<UINT>(h), 1, 1, format,
            {1, 0}, cpu ? D3D11_USAGE_STAGING : D3D11_USAGE_DEFAULT, bindings,
            cpu ? static_cast<UINT>(D3D11_CPU_ACCESS_READ) : 0u, 0};
        if (FAILED(device->CreateTexture2D(&desc, nullptr, &texture.data))) return false;
        if ((bindings & D3D11_BIND_SHADER_RESOURCE) &&
            FAILED(device->CreateShaderResourceView(texture.data.Get(), nullptr, &texture.read))) return false;
        if ((bindings & D3D11_BIND_UNORDERED_ACCESS) &&
            FAILED(device->CreateUnorderedAccessView(texture.data.Get(), nullptr, &texture.write))) return false;
        return true;
    }

    bool resize(const Parameters& p) {
        if (size.width == p.width && size.height == p.height &&
            size.outputWidth == p.outputWidth && size.outputHeight == p.outputHeight) return true;
        constexpr UINT read = D3D11_BIND_SHADER_RESOURCE;
        constexpr UINT write = D3D11_BIND_UNORDERED_ACCESS;
        constexpr auto bgra = DXGI_FORMAT_R8G8B8A8_UNORM;
        constexpr auto flow = DXGI_FORMAT_R16G16B16A16_FLOAT;
        const int blocksX = (p.width + 15) / 16, blocksY = (p.height + 15) / 16;
        if (!create(previous, p.width, p.height, bgra, read) ||
            !create(next, p.width, p.height, bgra, read) ||
            !create(forward, blocksX, blocksY, flow, read | write) ||
            !create(backward, blocksX, blocksY, flow, read | write) ||
            !create(output, p.outputWidth, p.outputHeight, bgra, write) ||
            !create(staging, p.outputWidth, p.outputHeight, bgra, 0, true)) {
            size = {};
            return false;
        }
        size = p;
        return true;
    }

    void dispatch(ID3D11ComputeShader* shader, Texture& target, const Parameters& p, int x, int y) {
        auto* targetView = target.write.Get();
        context->UpdateSubresource(constants.Get(), 0, nullptr, &p, 0, 0);
        context->CSSetShader(shader, nullptr, 0);
        context->CSSetUnorderedAccessViews(0, 1, &targetView, nullptr);
        context->Dispatch(x, y, 1);
        targetView = nullptr;
        context->CSSetUnorderedAccessViews(0, 1, &targetView, nullptr);
    }

    bool render(const uint8_t* a, const uint8_t* b, uint8_t* result, Parameters p) {
        if (!resize(p)) return false;
        auto* constantBuffer = constants.Get();
        auto* linear = sampler.Get();
        context->CSSetConstantBuffers(0, 1, &constantBuffer);
        context->CSSetSamplers(0, 1, &linear);
        context->UpdateSubresource(previous.data.Get(), 0, nullptr, a, p.width * 4, 0);
        ID3D11ShaderResourceView* inputs[4]{previous.read.Get(), nullptr, nullptr, nullptr};
        if (p.phase > 0) {
            context->UpdateSubresource(next.data.Get(), 0, nullptr, b, p.width * 4, 0);
            inputs[1] = next.read.Get();
            context->CSSetShaderResources(0, 4, inputs);
            dispatch(estimate.Get(), forward, p, (p.width + 127) / 128, (p.height + 127) / 128);
            p.direction = 1;
            dispatch(estimate.Get(), backward, p, (p.width + 127) / 128, (p.height + 127) / 128);
            inputs[2] = forward.read.Get();
            inputs[3] = backward.read.Get();
        }
        context->CSSetShaderResources(0, 4, inputs);
        dispatch(compose.Get(), output, p, (p.outputWidth + 15) / 16, (p.outputHeight + 15) / 16);
        std::fill_n(inputs, 4, nullptr);
        context->CSSetShaderResources(0, 4, inputs);

        context->CopyResource(staging.data.Get(), output.data.Get());
        D3D11_MAPPED_SUBRESOURCE surface{};
        if (FAILED(context->Map(staging.data.Get(), 0, D3D11_MAP_READ, 0, &surface))) return false;
        for (int row = 0; row < p.outputHeight; ++row)
            std::memcpy(result + size_t(row) * p.outputWidth * 4,
                static_cast<const uint8_t*>(surface.pData) + size_t(row) * surface.RowPitch,
                size_t(p.outputWidth) * 4);
        context->Unmap(staging.data.Get(), 0);
        return true;
    }
};
}

extern "C" {
__declspec(dllexport) Engine* ovrix_create() noexcept {
    try {
        auto engine = std::make_unique<Engine>();
        return engine->initialize() ? engine.release() : nullptr;
    } catch (...) { return nullptr; }
}

__declspec(dllexport) int ovrix_render(Engine* context, const uint8_t* a, const uint8_t* b, uint8_t* result,
    int w, int h, int outW, int outH, int radius, float phase, float sharpen) noexcept {
    if (!context || !a || !b || !result || w < 1 || h < 1 || outW < 1 || outH < 1 ||
        w > 4096 || h > 4096 || outW > 4096 || outH > 4096 || radius < 2 || radius > 24) return 0;
    return context->render(a, b, result, {w, h, outW, outH, radius, 0, phase, sharpen}) ? 1 : 0;
}

__declspec(dllexport) void ovrix_destroy(Engine* context) noexcept { delete context; }
}
