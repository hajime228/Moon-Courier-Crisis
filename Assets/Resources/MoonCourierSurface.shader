Shader "MoonCourierCrisis/Surface"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.88,0.90,0.94,1)
        _AlbedoTex ("LRO Regolith", 2D) = "gray" {}
        _NormalTex ("LRO Detail", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0,1)) = 0.30
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_AlbedoTex);
            SAMPLER(sampler_AlbedoTex);
            TEXTURE2D(_NormalTex);
            SAMPLER(sampler_NormalTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _NormalStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
            };

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34,456.21));
                p += dot(p,p + 45.32);
                return frac(p.x*p.y);
            }

            float Noise2D(float2 p)
            {
                float2 i=floor(p);
                float2 f=frac(p);
                f=f*f*(3.0-2.0*f);
                float a=Hash21(i);
                float b=Hash21(i+float2(1,0));
                float c=Hash21(i+float2(0,1));
                float d=Hash21(i+float2(1,1));
                return lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
            }

            float MareMask(float2 p, float2 center, float2 radius)
            {
                float2 q=(p-center)/radius;
                float d=length(q);
                float edge=(Noise2D(p*.075+center*.11)-.5)*.12;
                return 1.0-smoothstep(.72+edge,1.06+edge,d);
            }

            void CraterTone(float2 p, float2 center, float radius,
                            inout float interiorDark, inout float rimLight)
            {
                float d=distance(p,center)/radius;
                float inside=1.0-smoothstep(.22,1.0,d);
                float rim=exp(-pow((d-1.03)/.115,2.0));

                interiorDark=max(interiorDark,inside);
                rimLight=max(rimLight,rim);
            }

            Varyings Vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs pos=GetVertexPositionInputs(input.positionOS.xyz);
                o.positionHCS=pos.positionCS;
                o.positionWS=pos.positionWS;
                o.normalWS=TransformObjectToWorldNormal(input.normalOS);
                o.uv=input.uv;
                o.shadowCoord=GetShadowCoord(pos);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 baseNormal=normalize(input.normalWS);

                // Real Lunar Reconnaissance Orbiter surface detail.
                half3 tex=SAMPLE_TEXTURE2D(_AlbedoTex,sampler_AlbedoTex,input.uv).rgb;

                half3 encoded=SAMPLE_TEXTURE2D(_NormalTex,sampler_NormalTex,input.uv).rgb;
                float2 micro=encoded.rg*2.0-1.0;
                half3 detailNormal=normalize(baseNormal + half3(
                    micro.x*_NormalStrength,
                    0,
                    micro.y*_NormalStrength
                ));

                half3 albedo=tex*_BaseColor.rgb;

                // Broad dark mare regions — smooth and subtle.
                float maria=max(
                    MareMask(input.positionWS.xz,float2(-15,4),float2(19.5,13.2)),
                    MareMask(input.positionWS.xz,float2(20,-1),float2(16,10.5))
                );
                albedo*=lerp(1.0,.84,maria*.70);

                // Visually reinforce the SAME large craters used by A*.
                float craterInterior=0.0;
                float craterRim=0.0;
                // Жёлтые заказы: реальные маршрутные кратеры.
                CraterTone(input.positionWS.xz,float2( 7.15,-0.95),1.58,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2(-4.70, 7.75),1.82,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2(-6.10,10.65),1.74,craterInterior,craterRim);

                // Высокорисковый медицинский сектор.
                CraterTone(input.positionWS.xz,float2( 3.0,10.3),2.35,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2( 1.3,12.9),2.15,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2( 6.8,12.7),2.05,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2( 1.1,17.4),1.92,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2( 6.6,17.2),1.82,craterInterior,craterRim);

                // Кратерный сектор буровой.
                CraterTone(input.positionWS.xz,float2(10.7, 6.8),2.22,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2(13.0, 8.5),1.88,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2(18.0, 9.5),2.12,craterInterior,craterRim);
                CraterTone(input.positionWS.xz,float2(16.8,14.2),1.78,craterInterior,craterRim);

                albedo*=lerp(1.0,.72,craterInterior*.48);
                albedo*=1.0 + craterRim*.12;

                // The slopes themselves carry much of the lunar look.
                float slope=1.0-saturate(baseNormal.y);
                albedo*=1.0-slope*.29;

                // Tiny non-repeating regolith grain.
                float grain=Noise2D(input.positionWS.xz*3.6+57.1);
                albedo*=.985+(grain-.5)*.028;

                Light mainLight=GetMainLight(input.shadowCoord);
                half ndl=saturate(dot(detailNormal,mainLight.direction));

                // Lunar surface: small ambient component, crisp direct light.
                half ambient=.105;
                half direct=ndl*1.08;
                half3 lightColor=max(mainLight.color.rgb,half3(.88,.90,.96));

                half3 color=albedo*(ambient+direct)*lightColor;
                color+=albedo*half3(.008,.010,.016);

                return half4(color,1.0);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }

    FallBack Off
}
