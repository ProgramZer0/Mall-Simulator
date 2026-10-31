// Store edit mode: paints over every pixel whose surface lies outside the store being edited,
// so nothing outside the plot is drawn, not even the outside half of a wall or floor that
// crosses into it. Used by a Full Screen Pass Renderer Feature on the edit camera's renderer.
//
// It does nothing unless StoreEditIsolation turns it on (_EditClipEnabled), and that only
// happens while the edit camera is drawing, so every other camera is untouched.
Shader "MallSim/Edit Plot Clip"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "EditPlotClip"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #define MAX_BOXES 16

            // Set from StoreEditIsolation.
            float _EditClipEnabled;
            float _EditClipBoxCount;
            float4x4 _EditClipBoxes[MAX_BOXES]; // world -> box space; the box is -0.5..0.5
            float4 _EditClipBackground;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                if (_EditClipEnabled < 0.5)
                    return color;

                // Rebuild the world position of whatever is drawn at this pixel.
            #if UNITY_REVERSED_Z
                float depth = SampleSceneDepth(uv);
                if (depth < 0.000001) return _EditClipBackground;   // nothing drawn (sky)
            #else
                float depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, SampleSceneDepth(uv));
                if (depth > 0.999999) return _EditClipBackground;
            #endif
                float3 worldPos = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);

                // Keep it only if it's inside one of the store's boxes.
                int count = (int)_EditClipBoxCount;
                for (int i = 0; i < MAX_BOXES; i++)
                {
                    if (i >= count) break;
                    float3 p = mul(_EditClipBoxes[i], float4(worldPos, 1.0)).xyz;
                    if (all(abs(p) <= 0.5)) return color;
                }
                return _EditClipBackground;
            }
            ENDHLSL
        }
    }
}
