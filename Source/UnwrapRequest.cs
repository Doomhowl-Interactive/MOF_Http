using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Mof.Http;

public sealed class UnwrapRequest : IValidatableObject
{
    [Required, Description("Wavefront OBJ mesh to unwrap. Only .obj files are accepted.")]
    public IFormFile File { get; set; } = null!;
    [Range(1, 65536), DefaultValue(1024), Description("Texture resolution used to calculate island gaps, in pixels.")]
    public int Resolution { get; set; } = 1024;
    [DefaultValue(false), Description("Separate all hard edges, useful for lightmaps and normal maps.")]
    public bool Separate { get; set; }
    [Range(0.000001, 1000000), DefaultValue(1.0), Description("Pixel aspect ratio for non-square textures.")]
    public double Aspect { get; set; } = 1;
    [DefaultValue(false), Description("Use mesh normals to classify polygons.")]
    public bool Normals { get; set; }
    [Range(1, 1000), DefaultValue(1), Description("Number of UDIM tiles.")]
    public int Udims { get; set; } = 1;
    [DefaultValue(false), Description("Allow identical parts to share UV space.")]
    public bool Overlap { get; set; }
    [DefaultValue(false), Description("Allow mirrored parts to share UV space.")]
    public bool Mirror { get; set; }
    [DefaultValue(false), Description("Scale UVs to world space, possibly beyond the zero-to-one range.")]
    public bool WorldScale { get; set; }
    [Range(1, 1000000), DefaultValue(1024), Description("Pixels per world unit when WorldScale is enabled.")]
    public int Density { get; set; } = 1024;
    [DefaultValue(0.0), Description("X coordinate of the point toward which seams are directed.")]
    public double CenterX { get; set; }
    [DefaultValue(0.0), Description("Y coordinate of the point toward which seams are directed.")]
    public double CenterY { get; set; }
    [DefaultValue(0.0), Description("Z coordinate of the point toward which seams are directed.")]
    public double CenterZ { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!double.IsFinite(Aspect) || !double.IsFinite(CenterX) || !double.IsFinite(CenterY) || !double.IsFinite(CenterZ))
            yield return new ValidationResult("Coordinates and aspect must be finite numbers.", [nameof(Aspect), nameof(CenterX), nameof(CenterY), nameof(CenterZ)]);
        if (File is not null && (File.Length == 0 || !Path.GetExtension(File.FileName).Equals(".obj", StringComparison.OrdinalIgnoreCase)))
            yield return new ValidationResult("Upload a nonempty .obj file.", [nameof(File)]);
    }

    public IReadOnlyList<string> Arguments(string input, string output) =>
    [
        input, output,
        "-resolution", Resolution.ToString(CultureInfo.InvariantCulture),
        "-separate", Bool(Separate), "-aspect", Aspect.ToString("R", CultureInfo.InvariantCulture),
        "-normals", Bool(Normals), "-udims", Udims.ToString(CultureInfo.InvariantCulture),
        "-overlap", Bool(Overlap), "-mirror", Bool(Mirror), "-worldscale", Bool(WorldScale),
        "-density", Density.ToString(CultureInfo.InvariantCulture), "-center",
        CenterX.ToString("R", CultureInfo.InvariantCulture), CenterY.ToString("R", CultureInfo.InvariantCulture), CenterZ.ToString("R", CultureInfo.InvariantCulture)
    ];
    private static string Bool(bool value) => value ? "TRUE" : "FALSE";
}
