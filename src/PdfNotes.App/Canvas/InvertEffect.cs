using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PdfNotes.App.Canvas;

/// <summary>Inverts colors (spec 24.12 reading dark page). Shader: Canvas/Shaders/InvertColors.fx, compiled to .ps.</summary>
public sealed class InvertEffect : ShaderEffect
{
    private static PixelShader? shader;

    private static PixelShader Shader
    {
        get
        {
            if (shader is not null) return shader;
            using var stream = typeof(InvertEffect).Assembly.GetManifestResourceStream("PdfNotes.InvertColors.ps")
                ?? throw new InvalidOperationException("The invert shader resource is missing.");
            var loaded = new PixelShader();
            loaded.SetStreamSource(stream);
            loaded.Freeze();
            return shader = loaded;
        }
    }

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(InvertEffect), 0);

    public InvertEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
}
