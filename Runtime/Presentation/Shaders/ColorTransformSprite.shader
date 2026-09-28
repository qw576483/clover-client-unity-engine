// 逐实例「颜色变换」着色器（精灵侧：`SpriteRenderer`）。
//
// 顶点 / 片元与 Unity 内置精灵着色器同源：直接 include Unity 自带的 `UnitySprites.cginc`，
// 于是 `SpriteVert`（`OUT.color = IN.color * _Color * _RendererColor`）与 `SampleSpriteTexture`
// （`_MainTex` 由 `SpriteRenderer` 逐 sprite 写入）都保持原样 —— 逐 renderer 的染色 / 翻转
// （`_RendererColor` / `_Flip`）与共享材质都不受影响。
//
// 本文件只加一条变换：`_ColorMul` / `_ColorAdd`（默认值 = 恒等 ⇒ 与 `Sprites/Default` 逐像素一致）。
//
// 数值口径（`.sc` 颜色变换记录 `09`，字段序 `add.r, add.g, add.b, alpha, mul.r, mul.g, mul.b`）：
//   out_rgb = clamp(src.rgb * mul/255 + add/255)
//   out_a   = clamp(src.a * alpha/255)
// `255` = 1.0；alpha 只做乘算（没有加项）。材质把 `alpha` 并进 `_ColorMul.a`。
//
// 混合口径与 `Sprites/Default` 一致：预乘 + `Blend One OneMinusSrcAlpha`
//（片元里 `c.rgb *= c.a`，见 Unity 内置 `Sprites-Default.shader`）。
Shader "Clover/ColorTransform/Sprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ColorMul ("Color Mul (color transform)", Color) = (1,1,1,1)
        _ColorAdd ("Color Add (color transform)", Color) = (0,0,0,0)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
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

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            Name "ColorTransformSprite"

            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment ColorTransformSpriteFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA

            #include "UnityCG.cginc"
            #include "UnitySprites.cginc"

            fixed4 _ColorMul;
            fixed4 _ColorAdd;

            fixed4 ColorTransformSpriteFrag(v2f IN) : SV_Target
            {
                fixed4 s = SampleSpriteTexture(IN.texcoord);
                // 变换先作用在**图元自身**上，之后才乘顶点色（`SpriteRenderer.color` 仍是调用方的逐实例 tint）。
                s.rgb = saturate(s.rgb * _ColorMul.rgb + _ColorAdd.rgb);
                s.a = saturate(s.a * _ColorMul.a);

                fixed4 c = s * IN.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}
