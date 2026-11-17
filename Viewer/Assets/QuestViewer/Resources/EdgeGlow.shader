// Marvin's glow around the edges of your view (SolidWorks fork): clear in the middle, a soft coloured band towards
// the edges. Drawn on a quad just in front of the eyes, over everything. In Resources so it is always in the build.
Shader "QuestViewer/EdgeGlow"
{
    Properties
    {
        _Color ("Color", Color) = (0.35, 0.9, 1, 1)
        _Intensity ("Intensity", Range(0, 1)) = 0
        _Inner ("Clear radius (uv)", Float) = 0.42
        _Outer ("Full radius (uv)", Float) = 0.78
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay" "RenderType" = "Transparent" }

        Pass
        {
            Name "EdgeGlow"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Intensity, _Inner, _Outer;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Distance from the middle (0) to the quad's edge (1), a little squashed vertically.
                float2 p = (i.uv - 0.5) * 2;
                p.y *= 1.15;
                float d = length(p);
                half band = smoothstep(_Inner, _Outer, d);
                half a = saturate(band * _Intensity * 0.75h);
                return half4(_Color.rgb * a, a);
            }
            ENDHLSL
        }
    }
}
