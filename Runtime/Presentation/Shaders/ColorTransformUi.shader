// 逐实例「颜色变换」着色器（uGUI 侧：`Image` / `RawImage` 等 `Graphic`）。
//
// 与精灵侧（`Clover/ColorTransform/Sprite`）的数值口径完全一致（`.sc` 颜色变换记录 `09`）：
//   out_rgb = clamp(src.rgb * mul/255 + add/255)
//   out_a   = clamp(src.a * alpha/255)
// `255` = 1.0；alpha 只做乘算（没有加项）。材质把 `alpha` 并进 `_ColorMul.a`。
//
// 与精灵侧分开一份的原因：uGUI 的网格不提供 `_RendererColor` / `_Flip`，而 UI 需要
// 遮罩裁剪（`UnityUI.cginc` 的 `UnityGet2DClipping`）与 stencil；顶点色语义保持 UI 默认
//（`顶点色 × _Color`，`Image.color` 仍是调用方的逐实例 tint）。
//
// 默认值 = 恒等（`_ColorMul = 1` / `_ColorAdd = 0`）⇒ 不设这两项的材质与 `UI/Default` 逐像素一致。
Shader "Clover/ColorTransform/UI"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ColorMul ("Color Mul (color transform)", Color) = (1,1,1,1)
        _ColorAdd ("Color Add (color transform)", Color) = (0,0,0,0)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        // 混合与 `UI/Default` 一致：RGB 走 `SrcAlpha OneMinusSrcAlpha`、alpha 通道走
        // `One OneMinusSrcAlpha`（两者只在离屏 / RenderTexture 读回 alpha 时区分得出来）。
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "ColorTransformUi"

            CGPROGRAM
            #pragma vertex ColorTransformUiVert
            #pragma fragment ColorTransformUiFrag
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _ColorMul;
            fixed4 _ColorAdd;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;

            v2f ColorTransformUiVert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 ColorTransformUiFrag(v2f IN) : SV_Target
            {
                half4 s = tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd;
                s.rgb = saturate(s.rgb * _ColorMul.rgb + _ColorAdd.rgb);
                s.a = saturate(s.a * _ColorMul.a);

                fixed4 c = s * IN.color;

                #ifdef UNITY_UI_CLIP_RECT
                c.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(c.a - 0.001);
                #endif

                return c;
            }
            ENDCG
        }
    }

    Fallback "UI/Default"
}
