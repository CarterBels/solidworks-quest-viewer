// Translucent "holo-screen" for the part info panel (SolidWorks fork): a faint tinted pane with a glowing
// border and slowly drifting scan lines. In Resources so it is always included in the build.
Shader "QuestViewer/HoloPanel"
{
    Properties
    {
        _Color ("Color", Color) = (0.35, 0.9, 1, 1)
        _Alpha ("Pane alpha", Range(0, 1)) = 0.16
        _Edge ("Border width (uv)", Float) = 0.012
        _Aspect ("Width / height", Float) = 1.5
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" "RenderType" = "Transparent" }

        Pass
        {
            Name "HoloPanel"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Alpha, _Edge, _Aspect, _Fade;
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
                float2 uv = i.uv;
                // Distance to the nearest edge, in units of the panel height.
                float dx = min(uv.x, 1 - uv.x) * _Aspect;
                float dy = min(uv.y, 1 - uv.y);
                float d = min(dx, dy);
                half border = saturate(1 - d / _Edge);
                half glow = saturate(1 - d / (_Edge * 5)) * 0.35h;
                half scan = 0.75h + 0.25h * sin((uv.y * 90 - _Time.y * 2.5) * 3.14159);
                half a = saturate(_Alpha * scan + glow + border) * _Fade;
                return half4(_Color.rgb * a, a);
            }
            ENDHLSL
        }
    }
}
