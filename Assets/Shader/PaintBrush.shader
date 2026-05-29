Shader "Custom/PaintBrush"
{
    Properties
    {
        _MainTex    ("Current Texture", 2D)      = "white" {}
        _PaintPos   ("Paint UV Position", Vector) = (0,0,0,0)
        _PaintColor ("Paint Color", Color)         = (1,0,0,1)
        _BrushSize  ("Brush Size", Float)          = 0.05
        _Hardness   ("Hardness", Range(0,1))       = 0.8
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float4 _PaintPos;
            float4 _PaintColor;
            float  _BrushSize;
            float  _Hardness;

            struct Attributes { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);
                o.uv  = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 current = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                float dist    = distance(i.uv, _PaintPos.xy);
                float alpha   = 1.0 - smoothstep(_BrushSize * _Hardness, _BrushSize, dist);
                return lerp(current, _PaintColor, alpha * _PaintColor.a);
            }
            ENDHLSL
        }
    }
}