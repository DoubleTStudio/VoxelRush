// Voxel atlas shader: UV0 is the face position in block units (greedy quads span several blocks), UV1.x the atlas
// tile index. frac() repeats one tile per block; point sampling avoids bleeding between tiles.
Shader "VoxelRush/VoxelAtlas"
{
    Properties
    {
        [NoScaleOffset] _Atlas ("Atlas (16x16 tiles)", 2D) = "white" {}
        _Ambient ("Ambient", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Atlas);
            SAMPLER(sampler_point_clamp);

            CBUFFER_START(UnityPerMaterial)
                float _Ambient;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 blockUv : TEXCOORD0;
                float2 tile : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 blockUv : TEXCOORD1;
                float tile : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.blockUv = input.blockUv;
                output.tile = input.tile.x;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                const float tilesPerRow = 16.0;
                float tile = floor(input.tile + 0.5);
                float2 cell = float2(fmod(tile, tilesPerRow), floor(tile / tilesPerRow));
                // Atlas rows run top-down; texture v runs bottom-up.
                float2 uv = (cell + frac(input.blockUv)) / tilesPerRow;
                uv.y = 1.0 - uv.y;
                half4 albedo = SAMPLE_TEXTURE2D(_Atlas, sampler_point_clamp, uv);

                Light light = GetMainLight();
                half diffuse = saturate(dot(normalize(input.normalWS), light.direction));
                half3 color = albedo.rgb * (_Ambient + (1.0 - _Ambient) * diffuse * light.color);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
