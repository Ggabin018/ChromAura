#nullable enable
using Godot;

/// <summary>
/// Représente un corps détecté et suivi dans le temps via le capteur de profondeur Kinect.
/// Permet la persistance des identifiants et l'assignation stable des palettes entre chaque trame.
/// </summary>
public sealed class TrackedBody
{
	/// <summary>Identifiant numérique unique et incrémental du corps.</summary>
	public int Id { get; init; }

	/// <summary>Index de la palette de couleurs assignée à ce corps (0 à 4).</summary>
	public int PaletteIndex { get; set; }

	/// <summary>Position lissée du centre de masse (centroïde) dans l'espace coordonnées du masque.</summary>
	public Vector2 Centroid { get; set; }

	/// <summary>Boîte englobante (bounding box) du corps dans l'espace masque.</summary>
	public Rect2 BoundingBox { get; set; }

	/// <summary>Profondeur moyenne mesurée pour ce corps (valeur normalisée de 0.0 à 1.0).</summary>
	public float AvgDepth { get; set; }

	/// <summary>Nombre de cellules de la grille de profondeur constituant ce corps.</summary>
	public int PixelCount { get; set; }

	/// <summary>Nombre de trames consécutives pendant lesquelles ce corps n'a pas été apparié (gestion d'occlusion temporaire).</summary>
	public int MissedFrames { get; set; }

	/// <summary>Indique si une main effectuant le geste 'Pointing_Up' est actuellement associée à ce corps.</summary>
	public bool IsPointing { get; set; }
}
