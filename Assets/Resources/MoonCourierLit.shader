Shader "MoonCourierCrisis/Lit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Metallic ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="Geometry"
            "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 shadowCoord : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionHCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.shadowCoord = GetShadowCoord(pos);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 n = normalize(input.normalWS);
                Light mainLight = GetMainLight(input.shadowCoord);
                half ndl = saturate(dot(n,mainLight.direction));
                half shadow = mainLight.shadowAttenuation;

                half3 ambient = _BaseColor.rgb * half3(0.10,0.115,0.14);
                half3 diffuse = _BaseColor.rgb * mainLight.color * ndl * shadow;

                half3 viewDir = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half3 halfDir = SafeNormalize(mainLight.direction + viewDir);
                half spec = (half)pow(saturate(dot(n,halfDir)),lerp(12.0,72.0,(float)_Smoothness));
                half3 specColor = lerp(half3(0.12,0.12,0.12),_BaseColor.rgb,_Metallic);
                half3 specular = specColor * spec * lerp(0.06,0.42,_Smoothness) * shadow;

                return half4(ambient + diffuse + specular,_BaseColor.a);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }

    FallBack Off
}
