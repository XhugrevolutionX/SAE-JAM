Shader "Custom/UVMask"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float2 uv : TEXCOORD0; };
            struct Varyings   { float4 pos : SV_POSITION; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                // Remap UV (0..1) to clip space (-1..1)
                o.pos = float4(v.uv.x * 2 - 1, v.uv.y * 2 - 1, 0, 1);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return half4(1, 1, 1, 1); // white = valid UV area
            }
            ENDHLSL
        }
    }
}