Shader "LTCGI/LV Apply (Blit)"
{
    Properties
    {
        _LV_Volume ("Volume", 3D) = "black" {}
        _Directionality ("Directionality", Range(0.0, 2.0)) = 1.0
    }
    SubShader
    {
        Lighting Off
        Blend One Zero

        Pass
        {
            Name "LTCGI LV Apply"

            CGPROGRAM
            #include "UnityCG.cginc"
            //#define LTCGI_FAST_SAMPLING
            //#include "Packages/red.sim.lightvolumes/Shaders/LightVolumes.cginc" // open coded
            #include "Packages/at.pimaker.ltcgi/Shaders/LTCGI.cginc"

            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 5.0
            #pragma require geometry

            // keep in sync with U# adapter
            #define LV_MAX_SLICES 24
            uniform float _Udon_LTCGI_LV_LayerDepth;
            uniform float _Udon_LTCGI_LV_LayerOffset;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2g
            {
                float4 vertex : SV_POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex         : SV_POSITION;
                float3 globalTexcoord : TEXCOORD0;
                uint   slice          : SV_RenderTargetArrayIndex;
            };

            v2g vert(appdata v)
            {
                v2g o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            [maxvertexcount(3 * LV_MAX_SLICES)]
            void geom(triangle v2g input[3], inout TriangleStream<v2f> stream)
            {
                uint depth = (uint)_Udon_LTCGI_LV_LayerDepth;
                float invDepth = 1.0 / max((float)depth, 1.0);

                [loop]
                for (uint j = 0; j < LV_MAX_SLICES; ++j)
                {
                    uint s = j + (uint)_Udon_LTCGI_LV_LayerOffset;

                    // early out when we reach the actual slice count
                    if (s >= depth)
                        break;

                    float w = (s + 0.5) * invDepth;

                    [unroll]
                    for (int i = 0; i < 3; ++i)
                    {
                        v2f o;
                        o.vertex = input[i].vertex;
                        o.globalTexcoord = float3(input[i].uv, w);
                        o.slice = s;
                        stream.Append(o);
                    }
                    stream.RestartStrip();
                }
            }

            uniform Texture3D<float4> _LV_Volume;
            uniform SamplerState      sampler_LV_Volume;

            uniform float _Directionality;

            // from Light Volume Manager
            uniform float4 _UdonLightVolumeUvwScale[96];
            uniform float _UdonLightVolumeEnabled;
            uniform float _UdonLightVolumeCount;

            // from LTCGI LV Adapter
            uniform float4x4 _UdonLightVolumeFwdWorldMatrix[32];
            uniform float _UdonLightVolumesWithLTCGI; // actually a uint used as bitfield

            // special data for LVs from LTCGI_Adapter
            uniform uint _Udon_LTCGI_ScreenCount_LVs;
            uniform bool _Udon_LTCGI_Mask_LVs[MAX_SOURCES];

            static float4 uvwPos[3];
            static float3 padding; // assumes padding stays 1 in Texture3DAtlasGenerator

            float3 UVWinvert(float3 uvw, float3 uvwScale, float3 uvwPos)
            {
                return ((uvw - uvwPos) / uvwScale) - 0.5;
            }

            float3 WorldFromVolume(uint volumeID, float3 localPos)
            {
                return mul(_UdonLightVolumeFwdWorldMatrix[volumeID], float4(localPos, 1.0)).xyz;
            }

            bool PointLocalInAABB(float3 localUVW, float3 uvwScale)
            {
                float3 localPadding = padding / uvwScale; // in my head this should be * 0.5, but then it has seams again 🤔
                return all(abs(localUVW) <= 0.5 + localPadding);
            }

            void BasicLTCGI(float3 worldPos, inout float3 L0, inout float3 L1r, inout float3 L1g, inout float3 L1b)
            {
                // backup backed values and reset output data
                float3 L0b = L0;
                float3 L1b_r = L1r;
                float3 L1b_g = L1g;
                float3 L1b_b = L1b;
                L0 = L1r = L1g = L1b = 0;

                #if MAX_SOURCES != 1
                    uint count = min(_Udon_LTCGI_ScreenCount_LVs, MAX_SOURCES);
                    [loop]
                #else
                    // mobile config
                    const uint count = 1;
                    [unroll(1)]
                #endif
                for (uint i = 0; i < count; i++) {
                    // skip masked and black lights
                    if (_Udon_LTCGI_Mask_LVs[i]) continue;
                    float4 extra = _Udon_LTCGI_ExtraData[i];
                    float3 color = extra.rgb;
                    if (!any(color)) continue;

                    ltcgi_flags flags = ltcgi_parse_flags(asuint(extra.w), true);

                    #ifdef LTCGI_ALWAYS_LTC_DIFFUSE
                        // can't honor a lightmap-only light in this mode
                        if (flags.lmdOnly) continue;
                    #endif

                    #ifdef LTCGI_TOGGLEABLE_SPEC_DIFF_OFF
                        // compile branches below away statically
                        flags.diffuse = flags.specular = true;
                    #endif

                    if (!flags.diffuse) continue;

                    // calculate (shifted) world space positions
                    float3 Lw[4];
                    float4 uvStart = (float4)0, uvEnd = (float4)0;
                    bool isTri = false;
                    LTCGI_GetLw(i, flags, worldPos, Lw, uvStart, uvEnd, isTri);

                    // skip single-sided lights that face the other way
                    float3 screenNorm = cross(Lw[1] - Lw[0], Lw[2] - Lw[0]);
                    if (!flags.doublesided) {
                        if (dot(screenNorm, Lw[0]) < 0)
                            continue;
                    }

                    ltcgi_input input;
                    input.i = i;
                    input.Lw = Lw;
                    input.isTri = isTri;
                    input.uvStart = uvStart;
                    input.uvEnd = uvEnd;
                    input.rawColor = color;
                    input.flags = flags;
                    input.screenNormal = screenNorm;

                    // construct orthonormal basis around N by approximating view and normal dir as coming directly from the screen
                    float3 avgPos = (Lw[0] + Lw[1] + Lw[2] + Lw[3]) * 0.25f;
                    float3 worldNorm = normalize(screenNorm);
                    float3 viewDir = -normalize(avgPos);
                    float3 T1 = normalize(viewDir - worldNorm*dot(viewDir, worldNorm));
                    float3 T2 = cross(worldNorm, T1);
                    float3x3 identityBrdf = float3x3(float3(T1), float3(T2), float3(worldNorm));

                    ltcgi_output diff;
                    LTCGI_Evaluate(input, identityBrdf, 0, true, diff);

                    float l0ch = flags.lmch == 0 ? 1 : L0b[flags.lmch - 1];
                    float3 l1ch;
                    switch (flags.lmch)
                    {
                        case 1: l1ch = L1b_r; break;
                        case 2: l1ch = L1b_g; break;
                        case 3: l1ch = L1b_b; break;
                        default: l1ch = 0; break; // no channel (or 0 aka "off"), skips L1
                    }

                    if (flags.lmdOnly)
                    {
                        // hack to make lightmap-only lights have smooter falloff, since diff.intensity will always be 0
                        l0ch = min(1, l0ch * l0ch);
                        diff.intensity = 1;
                    }

                    // variables:
                    // L0b = baked L0 from single-color LTCGI screen
                    // L1b_[rgb] = baked L1 basis from single-color LTCGI screen
                    // flags.lmch = lightmap channel configured on the LTCGI_Screen, i.e. which channel of the baked data to use
                    // l0ch = the R, G or B value extracted from L0b based on flags.lmch, used as the basis to propagate to all channels in the output L0
                    // l1ch = based on l0ch, selected from L1b_[rgb] to use as the basis to propagate to all channels in the output L1
                    // rgbL0 = LTC output spread across RGB channels multiplied by l0ch (baked intensity)

                    float3 output = diff.color * diff.intensity;
                    float3 rgbL0 = max(0, l0ch) * output;

                    float safeL0 = max(saturate(l0ch), 1e-4);
                    float3 l1Basis = l1ch / safeL0;

                    l1Basis *= _Directionality; // fudge factor for artistic control
                    float lenL1 = length(l1Basis);
                    if (lenL1 > 0.98)
                        l1Basis *= 0.98 / lenL1;

                    L0 += rgbL0;
                    L1r += l1Basis * rgbL0.r;
                    L1g += l1Basis * rgbL0.g;
                    L1b += l1Basis * rgbL0.b;
                }
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 uvw = i.globalTexcoord.xyz;
                float4 val = _LV_Volume.SampleLevel(sampler_LV_Volume, uvw, 0);

                if (!_UdonLightVolumeEnabled)
                    return val;

                // calculate padding from dimension side (one sample in texture space)
                uint3 dim = 0;
                _LV_Volume.GetDimensions(dim.x, dim.y, dim.z);
                padding = float3(1.0f / dim.x, 1.0f / dim.y, 1.0f / dim.z);

                uint ltcgiVolumes = asuint(_UdonLightVolumesWithLTCGI);

                uint volumesCount = clamp((uint) _UdonLightVolumeCount, 0, 32);
                [loop] for (uint id = 0; id < volumesCount; id++)
                {
                    if ((ltcgiVolumes & (1u << id)) == 0)
                        continue;

                    uint uvwID = id * 3;
                    uvwPos[0] = _UdonLightVolumeUvwScale[uvwID];
                    uvwPos[1] = _UdonLightVolumeUvwScale[uvwID + 1];
                    uvwPos[2] = _UdonLightVolumeUvwScale[uvwID + 2];
                    float3 uvwScale = float3(uvwPos[0].w, uvwPos[1].w, uvwPos[2].w);

                    [loop] for (uint level = 0; level < 3; level++)
                    {
                        float3 localUVW = UVWinvert(uvw, uvwScale, uvwPos[level].xyz);
                        if (PointLocalInAABB(localUVW, uvwScale))
                        {
                            /*
                                packed format (from LV code):
                                    L0 = tex0.rgb;
                                    L1r = float3(tex1.r, tex2.r, tex0.a);
                                    L1g = float3(tex1.g, tex2.g, tex1.a);
                                    L1b = float3(tex1.b, tex2.b, tex2.a);
                            */

                            float3 L0 = 0, L1r = 0, L1g = 0, L1b = 0;
                            [forcecase] switch (level)
                            {
                                case 0: L0.rgb = val.rgb; L1r.z = val.a; break;
                                case 1: L1r.x = val.r; L1g.xz = val.ga; L1b.x = val.b; break;
                                case 2: L1r.y = val.r; L1g.y = val.g; L1b.yz = val.ba; break;
                            }

                            float3 worldPos = WorldFromVolume(id, localUVW);
                            BasicLTCGI(worldPos, L0, L1r, L1g, L1b);

                            [forcecase] switch (level)
                            {
                                case 0: val.rgb = L0; val.a = L1r.z; break;
                                case 1: val.r = L1r.x; val.ga = L1g.xz; val.b = L1b.x; break;
                                case 2: val.r = L1r.y; val.g = L1g.y; val.ba = L1b.yz; break;
                            }

                            return val; // exit now, we found our target volume and applied LTCGI data
                        }
                    }
                }

                return val; // pass through unrelated data
            }
            ENDCG
        }
    }
}
