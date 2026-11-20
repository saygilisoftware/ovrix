// Input textures store BGRA bytes in RGBA resources, so R and B labels are reversed.
Texture2D<float4> previousFrame : register(t0);
Texture2D<float4> nextFrame : register(t1);
Texture2D<float4> forwardFlow : register(t2);
Texture2D<float4> backwardFlow : register(t3);
RWTexture2D<float4> destination : register(u0);
SamplerState linearClamp : register(s0);

cbuffer FrameParameters : register(b0)
{
    int2 sourceSize;
    int2 outputSize;
    int searchRadius;
    int direction;
    float phase;
    float sharpen;
};

float luminance(float4 bgra)
{
    return dot(bgra.rgb, float3(0.0722, 0.7152, 0.2126));
}

float patchError(int2 center, int2 displacement)
{
    float error = 0;
    [unroll]
    for (int y = -4; y <= 4; y += 4)
    {
        [unroll]
        for (int x = -4; x <= 4; x += 4)
        {
            int2 a = clamp(center + int2(x, y), 0, sourceSize - 1);
            int2 b = clamp(a + displacement, 0, sourceSize - 1);
            float4 start = direction == 0 ? previousFrame[a] : nextFrame[a];
            float4 finish = direction == 0 ? nextFrame[b] : previousFrame[b];
            error += abs(luminance(start) - luminance(finish));
        }
    }
    return error / 9 + 0.00045 * (abs(displacement.x) + abs(displacement.y));
}

[numthreads(8, 8, 1)]
void Estimate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2(sourceSize) + 15) / 16)) return;
    int2 center = min(int2(id.xy) * 16 + 8, sourceSize - 1);
    int2 best = 0;
    float error = patchError(center, best);
    if (error > 0.006)
    {
        int levels = searchRadius <= 8 ? 2 : 3;
        int stepSize = max(2, (searchRadius + levels - 1) / levels);
        [loop]
        for (int y = -levels; y <= levels; y++)
        {
            [loop]
            for (int x = -levels; x <= levels; x++)
            {
                int2 candidate = clamp(int2(x, y) * stepSize, -searchRadius, searchRadius);
                float score = patchError(center, candidate);
                if (score < error) { error = score; best = candidate; }
            }
        }
        int2 coarse = best;
        [unroll]
        for (int y2 = -2; y2 <= 2; ++y2)
        {
            [unroll]
            for (int x2 = -2; x2 <= 2; ++x2)
            {
                int2 candidate = coarse + int2(x2, y2);
                float score = patchError(center, candidate);
                if (score < error) { error = score; best = candidate; }
            }
        }
    }
    destination[id.xy] = float4(best, error, 0);
}

[numthreads(16, 16, 1)]
void Compose(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= uint2(outputSize))) return;
    float2 uv = (id.xy + 0.5) / float2(outputSize);
    float4 color = previousFrame.SampleLevel(linearClamp, uv, 0);
    if (phase > 0)
    {
        float2 blocks = (uint2(sourceSize) + 15) / 16;
        float2 flowUv = uv * sourceSize / (blocks * 16);
        float3 f = forwardFlow.SampleLevel(linearClamp, flowUv, 0).xyz;
        float3 b = backwardFlow.SampleLevel(linearClamp, flowUv, 0).xyz;
        float4 a = previousFrame.SampleLevel(linearClamp, uv - f.xy * phase / sourceSize, 0);
        float4 c = nextFrame.SampleLevel(linearClamp, uv - b.xy * (1 - phase) / sourceSize, 0);
        float inconsistency = length(f.xy + b.xy);
        float blend = inconsistency < 7 && max(f.z, b.z) < 0.20 ? phase : (f.z < b.z ? 0 : 1);
        color = lerp(a, c, blend);
    }
    if (sharpen > 0)
    {
        float2 delta = 1.0 / sourceSize;
        float3 neighbors = previousFrame.SampleLevel(linearClamp, uv + float2(delta.x, 0), 0).rgb
                         + previousFrame.SampleLevel(linearClamp, uv - float2(delta.x, 0), 0).rgb
                         + previousFrame.SampleLevel(linearClamp, uv + float2(0, delta.y), 0).rgb
                         + previousFrame.SampleLevel(linearClamp, uv - float2(0, delta.y), 0).rgb;
        if (phase == 0) color.rgb = saturate(color.rgb + (color.rgb - neighbors * 0.25) * sharpen);
    }
    destination[id.xy] = float4(color.rgb, 1);
}
