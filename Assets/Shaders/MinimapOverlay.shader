Shader "UI/MinimapOverlay"
{
    Properties
    {
        _MainTex        ("Base Map",          2D)      = "white" {}

        // Player cross
        _PlayerPos      ("Player UV Pos",     Vector)  = (0.5, 0.5, 0, 0)
        _PlayerColor    ("Player Color",      Color)   = (1, 0.2, 0.2, 1)
        // Cross arm half-length in UV units (set from C# as tiles/gridSize)
        _CrossArm       ("Cross Arm UV",      Vector)  = (0.05, 0.05, 0, 0)

        // Camera wireframe rect (xmin, ymin, xmax, ymax) in UV units
        _CamRect        ("Camera Rect UV",    Vector)  = (0.2, 0.2, 0.8, 0.8)
        _CamColor       ("Camera Color",      Color)   = (1, 1, 0.3, 1)

        // Half line width in UV space, set from C# each frame: lineWidthPx * 0.5 / displaySizePx
        // Stored as (halfX, halfY) to handle non-square displays correctly.
        _HalfLineUV     ("Half Line UV",      Vector)  = (0.01, 0.01, 0, 0)

        // Required by Unity UI system
        _StencilComp    ("Stencil Comparison",Float)   = 8
        _Stencil        ("Stencil ID",        Float)   = 0
        _StencilOp      ("Stencil Operation", Float)   = 0
        _StencilWriteMask("Stencil Write Mask",Float)  = 255
        _StencilReadMask ("Stencil Read Mask", Float)  = 255
        _ColorMask      ("Color Mask",        Float)   = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"             = "Transparent"
            "IgnoreProjector"   = "True"
            "RenderType"        = "Transparent"
            "PreviewType"       = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref       [_Stencil]
            Comp      [_StencilComp]
            Pass      [_StencilOp]
            ReadMask  [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull    Off
        Lighting Off
        ZWrite  Off
        ZTest   [unity_GUIZTestMode]
        Blend   SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   3.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 uv       : TEXCOORD0;
                float4 worldPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4    _MainTex_ST;
            float4    _ClipRect;

            float2 _PlayerPos;
            fixed4 _PlayerColor;
            float2 _CrossArm;

            float4 _CamRect;
            fixed4 _CamColor;

            float2 _HalfLineUV;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPos = v.vertex;
                o.vertex   = UnityObjectToClipPos(v.vertex);
                o.uv       = TRANSFORM_TEX(v.uv, _MainTex);
                o.color    = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // Base map
                fixed4 col = tex2D(_MainTex, uv) * i.color;

                // _HalfLineUV is pre-computed in C# as: lineWidthPx * 0.5 / displaySizePx
                // This is independent of fwidth, so it works correctly under any Canvas scale.

                // ── Camera wireframe rect ────────────────────────────────────
                bool inH = uv.x >= _CamRect.x && uv.x <= _CamRect.z;
                bool inV = uv.y >= _CamRect.y && uv.y <= _CamRect.w;

                bool onLeft   = abs(uv.x - _CamRect.x) < _HalfLineUV.x && inV;
                bool onRight  = abs(uv.x - _CamRect.z) < _HalfLineUV.x && inV;
                bool onBottom = abs(uv.y - _CamRect.y) < _HalfLineUV.y && inH;
                bool onTop    = abs(uv.y - _CamRect.w) < _HalfLineUV.y && inH;

                if (onLeft || onRight || onBottom || onTop)
                    col = _CamColor;

                // ── Player cross ─────────────────────────────────────────────
                float2 d = abs(uv - _PlayerPos);
                bool onCrossH = d.y < _HalfLineUV.y && d.x < _CrossArm.x;
                bool onCrossV = d.x < _HalfLineUV.x && d.y < _CrossArm.y;

                if (onCrossH || onCrossV)
                    col = _PlayerColor;

                // UI clipping
                col.a *= UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                #ifdef UNITY_UI_ALPHACLIP
                clip(col.a - 0.001);
                #endif

                return col;
            }
            ENDCG
        }
    }
}
