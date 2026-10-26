// Reading dark page (spec 24.12): inverts the colors of the page display. WPF passes premultiplied alpha.
// Compiled with tools/compile-shader.py into InvertColors.ps (ps_2_0), embedded as a resource.
sampler2D input : register(s0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    c.rgb = c.a - c.rgb;
    return c;
}
